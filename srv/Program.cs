using System.Text.Json;
using System.Text.RegularExpressions;
using Sap.Data.Hana;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Bind to CF port if provided
var cfPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(cfPort))
{
    app.Urls.Add($"http://0.0.0.0:{cfPort}");
}

app.UseSwagger();
app.UseSwaggerUI();

// ----------------------------------------------------------------------
// Mock products (fallback if HANA connection fails)
// ----------------------------------------------------------------------
var mockProducts = new List<Product>
{
    new(1, "Laptop",   999.99m, DateTime.UtcNow.AddDays(-30)),
    new(2, "Mouse",     29.99m, DateTime.UtcNow.AddDays(-15)),
    new(3, "Keyboard",  79.99m, DateTime.UtcNow.AddDays(-7))
};

app.MapGet("/api/health", () =>
{
    return Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
});

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
    var product = new Product(id, dto.Name, dto.Price, DateTime.UtcNow);
    mockProducts.Add(product);
    return Results.Created($"/api/products/{product.Id}", product);
});

app.MapPut("/api/products/{id}", (int id, UpdateProductDto dto) =>
{
    var existing = mockProducts.FirstOrDefault(p => p.Id == id);
    if (existing is null)
        return Results.NotFound();

    var index = mockProducts.IndexOf(existing);
    mockProducts[index] = existing with { Name = dto.Name, Price = dto.Price };
    return Results.Ok(mockProducts[index]);
});

app.MapDelete("/api/products/{id}", (int id) =>
{
    var existing = mockProducts.FirstOrDefault(p => p.Id == id);
    if (existing is null)
        return Results.NotFound();

    mockProducts.Remove(existing);
    return Results.Ok();
});

app.Run();

// ======================================================================
// HANA helper
// ======================================================================

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

        // Look for any service group whose name contains "hana"
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

                string? host = null;
                string? port = null;
                string? user = null;
                string? pwd  = null;
                string? schema = null;

                if (creds.TryGetProperty("host", out var je))   host   = je.GetString();
                if (creds.TryGetProperty("port", out je))       port   = je.GetString() ?? je.GetRawText().Trim('"');
                if (creds.TryGetProperty("user", out je))       user   = je.GetString();
                if (creds.TryGetProperty("password", out je))   pwd    = je.GetString();
                if (creds.TryGetProperty("schema", out je))     schema = je.GetString();

                // HDI-style credentials sometimes use hdi_user / hdi_password
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

                if (string.IsNullOrWhiteSpace(port))
                    port = "443";

                var server = $"{host}:{port}";

                var csb = new HanaConnectionStringBuilder
                {
                    Server  = server,
                    UserID  = user,
                    Password = pwd
                };

                if (!string.IsNullOrWhiteSpace(schema))
                    csb.CurrentSchema = schema;

                // IMPORTANT: DO NOT add ValidateCertificate / ServerNode / Port here.
                // This older driver only supports a limited set of keywords.

                var connStr = csb.ConnectionString;

                // Mask password in logs
                var safeConnStr = Regex.Replace(
                    connStr,
                    @"(Password|PWD)\s*=\s*[^;]*",
                    "$1=***",
                    RegexOptions.IgnoreCase);

                Console.Error.WriteLine($"[HANA] Using connection string: {safeConnStr}");

                var products = new List<Product>();

                using (var conn = new HanaConnection(connStr))
                {
                    await conn.OpenAsync();

                    const string sql =
                        "SELECT \"ID\",\"NAME\",\"PRICE\",\"CREATEDAT\" " +
                        "FROM \"PRODUCTS\" ORDER BY \"ID\"";

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
                }

                Console.Error.WriteLine($"[HANA] Retrieved {products.Count} products from HANA.");
                return products;
            }
        }

        Console.Error.WriteLine("[HANA] No matching HANA service in VCAP_SERVICES.");
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HANA] GetProductsFromHana failed: {ex}");
        return null;
    }
}

// ======================================================================
// DTOs / records
// ======================================================================

public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
