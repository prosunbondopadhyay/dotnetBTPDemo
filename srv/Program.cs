using Sap.Data.Hana;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Bind to CF port if provided
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    app.Urls.Add($"http://0.0.0.0:{port}");
}

app.UseSwagger();
app.UseSwaggerUI();

// Mock products data (used as fallback when HANA is not reachable)
var mockProducts = new List<Product>
{
    new(1, "Laptop",   999.99m, DateTime.UtcNow.AddDays(-30)),
    new(2, "Mouse",     29.99m, DateTime.UtcNow.AddDays(-15)),
    new(3, "Keyboard",  79.99m, DateTime.UtcNow.AddDays(-7))
};

app.MapGet("/api/health", () =>
    Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Try to fetch products from HANA; fall back to mock data
app.MapGet("/api/products", async () =>
{
    var hanaProducts = await GetProductsFromHana();
    var source = hanaProducts ?? mockProducts;
    return Results.Ok(source.OrderBy(p => p.Id));
});

app.MapGet("/api/products/{id}", async (int id) =>
{
    var hanaProducts = await GetProductsFromHana();
    var source = hanaProducts ?? mockProducts;
    var product = source.FirstOrDefault(p => p.Id == id);
    return product is not null ? Results.Ok(product) : Results.NotFound();
});

app.MapPost("/api/products", (CreateProductDto dto) =>
{
    var id = mockProducts.Max(p => p.Id) + 1;
    var newProduct = new Product(id, dto.Name, dto.Price, DateTime.UtcNow);
    mockProducts.Add(newProduct);
    return Results.Created($"/api/products/{newProduct.Id}", newProduct);
});

app.MapPut("/api/products/{id}", (int id, UpdateProductDto dto) =>
{
    var product = mockProducts.FirstOrDefault(p => p.Id == id);
    if (product is null)
        return Results.NotFound();

    var index = mockProducts.IndexOf(product);
    mockProducts[index] = product with { Name = dto.Name, Price = dto.Price };
    return Results.Ok(mockProducts[index]);
});

app.MapDelete("/api/products/{id}", (int id) =>
{
    var product = mockProducts.FirstOrDefault(p => p.Id == id);
    if (product is null)
        return Results.NotFound();

    mockProducts.Remove(product);
    return Results.Ok();
});

// Simple diagnostic endpoint
app.MapGet("/api/diagnose-hana", async () =>
{
    var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
    var hanaProducts = await GetProductsFromHana();

    return Results.Ok(new
    {
        vcapPresent = !string.IsNullOrWhiteSpace(vcap),
        productsFromHana = hanaProducts?.Count ?? 0,
        usedMock = hanaProducts is null
    });
});

app.Run();


// --------- LOCAL FUNCTION: HANA ACCESS ----------

async Task<List<Product>?> GetProductsFromHana()
{
    try
    {
        var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
        if (string.IsNullOrWhiteSpace(vcap))
        {
            Console.Error.WriteLine("[HANA] VCAP_SERVICES not found or empty.");
            return null;
        }

        using var doc = JsonDocument.Parse(vcap);

        // Look for any HANA-like service group (hana, hana-cloud, etc.)
        foreach (var group in doc.RootElement.EnumerateObject())
        {
            if (!group.Name.Contains("hana", StringComparison.OrdinalIgnoreCase))
                continue;

            if (group.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in group.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("credentials", out var creds))
                    continue;

                string? host    = null;
                string? portStr = null;
                string? user    = null;
                string? pwd     = null;
                string? schema  = null;

                if (creds.TryGetProperty("host", out var je))
                    host = je.GetString();

                if (creds.TryGetProperty("port", out je))
                    portStr = je.ValueKind == JsonValueKind.String
                        ? je.GetString()
                        : je.GetRawText();

                if (creds.TryGetProperty("user", out je))
                    user = je.GetString();

                if (creds.TryGetProperty("password", out je))
                    pwd = je.GetString();

                if (creds.TryGetProperty("schema", out je))
                    schema = je.GetString();

                // HDI-style: sometimes user/pwd are under hdi_user / hdi_password
                if (string.IsNullOrWhiteSpace(user) && creds.TryGetProperty("hdi_user", out je))
                    user = je.GetString();

                if (string.IsNullOrWhiteSpace(pwd) && creds.TryGetProperty("hdi_password", out je))
                    pwd = je.GetString();

                if (string.IsNullOrWhiteSpace(host) ||
                    string.IsNullOrWhiteSpace(user) ||
                    string.IsNullOrWhiteSpace(pwd))
                {
                    Console.Error.WriteLine(
                        $"[HANA] Incomplete credentials. host='{host}', user='{user}', pwdEmpty={string.IsNullOrEmpty(pwd)}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(portStr))
                    portStr = "443";

                portStr = portStr.Trim('"');
                var hostPort = $"{host}:{portStr}";

                // Build several possible connection string variants – we don't know
                // exactly which keyword set this Sap.Data.Hana.Net.v8.0 build prefers,
                // so we try a few canonical ones.
                var schemaSuffix = string.IsNullOrWhiteSpace(schema)
                    ? ""
                    : $"CurrentSchema={schema};";

                var variants = new List<string>
                {
                    // Variant 1 – typical ADO.NET (Server + UserID/Password)
                    $"Server={hostPort};UserID={user};Password={pwd};Encrypt=True;ValidateCertificate=False;{schemaSuffix}",

                    // Variant 2 – ADO.NET with UID/PWD + lowercase encrypt properties
                    $"Server={hostPort};UID={user};PWD={pwd};encrypt=true;sslValidateCertificate=false;{schemaSuffix}",

                    // Variant 3 – 'serverNode' keyword as documented in HANA client ref
                    $"serverNode={hostPort};UID={user};PWD={pwd};encrypt=true;sslValidateCertificate=false;{schemaSuffix}",

                    // Variant 4 – Server + separate Port + UserID/Password
                    $"Server={host};Port={portStr};UserID={user};Password={pwd};Encrypt=True;ValidateCertificate=False;{schemaSuffix}",

                    // Variant 5 – Server + separate Port + UID/PWD
                    $"Server={host};Port={portStr};UID={user};PWD={pwd};encrypt=true;sslValidateCertificate=false;{schemaSuffix}"
                };

                Exception? lastEx = null;

                foreach (var cs in variants)
                {
                    if (string.IsNullOrWhiteSpace(cs))
                        continue;

                    var safe = cs;
                    if (!string.IsNullOrEmpty(pwd))
                        safe = safe.Replace(pwd, "***");

                    try
                    {
                        Console.Error.WriteLine($"[HANA] Trying connection string variant: {safe}");

                        using var conn = new HanaConnection(cs);
                        await conn.OpenAsync();

                        const string sql =
                            "SELECT \"ID\",\"name\",\"price\",\"createdAt\" " +
                            "FROM \"Products\" ORDER BY \"ID\"";

                        var products = new List<Product>();

                        using var cmd = new HanaCommand(sql, conn);
                        using var reader = await cmd.ExecuteReaderAsync();

                        while (await reader.ReadAsync())
                        {
                            var id        = reader.GetInt32(0);
                            var name      = reader.GetString(1);
                            var price     = reader.GetDecimal(2);
                            var createdAt = reader.GetDateTime(3);

                            products.Add(new Product(id, name, price, createdAt));
                        }

                        Console.Error.WriteLine($"[HANA] Variant succeeded, retrieved {products.Count} products.");
                        return products;
                    }
                    catch (ArgumentException ex)
                    {
                        // This is the "Invalid connection string" / unsupported-keyword case: try next variant
                        Console.Error.WriteLine($"[HANA] Variant failed with ArgumentException: {ex.Message}");
                        lastEx = ex;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        // Non-syntax error (network/auth/etc.) – further variants won't help much
                        Console.Error.WriteLine($"[HANA] Variant failed with non-ArgumentException: {ex}");
                        lastEx = ex;
                        break;
                    }
                }

                if (lastEx != null)
                {
                    Console.Error.WriteLine($"[HANA] All connection string variants failed. Last error: {lastEx.Message}");
                }

                // We tried all variants for this binding -> give up, fall back to mock
                return null;
            }
        }

        Console.Error.WriteLine("[HANA] No HANA service with credentials found in VCAP_SERVICES.");
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HANA] GetProductsFromHana failed: {ex}");
        return null;
    }
}

// --------- RECORD TYPES (AT THE BOTTOM) ----------

public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
