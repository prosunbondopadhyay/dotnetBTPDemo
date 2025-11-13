using System.Text.Json;
using System.Data.Odbc;
using System.Text;
using System.Text.Json.Nodes;

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

// Mock products data (in production, this would query HANA)
var mockProducts = new List<Product>
{
    new(1, "Laptop", 999.99m, DateTime.UtcNow.AddDays(-30)),
    new(2, "Mouse", 29.99m, DateTime.UtcNow.AddDays(-15)),
    new(3, "Keyboard", 79.99m, DateTime.UtcNow.AddDays(-7))
};

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Try to fetch products from HANA if connection info is available; otherwise fall back to mock data
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
    return product != null ? Results.Ok(product) : Results.NotFound();
});

app.MapPost("/api/products", async (CreateProductDto dto) =>
{
    var id = mockProducts.Max(p => p.Id) + 1;
    var newProduct = new Product(id, dto.Name, dto.Price, DateTime.UtcNow);
    mockProducts.Add(newProduct);
    return Results.Created($"/api/products/{newProduct.Id}", newProduct);
});

app.MapPut("/api/products/{id}", async (int id, UpdateProductDto dto) =>
{
    var product = mockProducts.FirstOrDefault(p => p.Id == id);
    if (product == null)
        return Results.NotFound();

    var index = mockProducts.IndexOf(product);
    mockProducts[index] = product with { Name = dto.Name, Price = dto.Price };
    return Results.Ok(mockProducts[index]);
});

app.MapDelete("/api/products/{id}", async (int id) =>
{
    var product = mockProducts.FirstOrDefault(p => p.Id == id);
    if (product == null)
        return Results.NotFound();

    mockProducts.Remove(product);
    return Results.Ok();
});

app.Run();

// Helper: try to read HANA credentials and query Products table via ODBC.
async Task<List<Product>?> GetProductsFromHana()
{
    try
    {
        // Build ODBC connection string from environment / VCAP
        var explicitOdbc = Environment.GetEnvironmentVariable("HANA_CONNECTION");
        string? odbcConnStr = null;

        if (!string.IsNullOrWhiteSpace(explicitOdbc))
        {
            odbcConnStr = explicitOdbc;
        }
        else
        {
            // Individual env vars
            var host = Environment.GetEnvironmentVariable("HANA_HOST");
            var port = Environment.GetEnvironmentVariable("HANA_PORT");
            var user = Environment.GetEnvironmentVariable("HANA_USER");
            var pwd = Environment.GetEnvironmentVariable("HANA_PASSWORD");

            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pwd))
            {
                var server = host + (string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port);
                odbcConnStr = $"Driver={{HDBODBC}};ServerNode={server};UID={user};PWD={pwd};";
            }
            else
            {
                // Try to parse VCAP_SERVICES (Cloud Foundry)
                var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
                if (!string.IsNullOrWhiteSpace(vcap))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(vcap);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (!item.TryGetProperty("credentials", out var creds)) continue;
                                string? h = null, p = null, u = null, pw = null;
                                if (creds.TryGetProperty("host", out var je)) h = je.GetString();
                                if (creds.TryGetProperty("hostname", out je) && string.IsNullOrWhiteSpace(h)) h = je.GetString();
                                if (creds.TryGetProperty("port", out je)) p = je.GetRawText().Trim('\"');
                                if (creds.TryGetProperty("user", out je)) u = je.GetString();
                                if (creds.TryGetProperty("username", out je) && string.IsNullOrWhiteSpace(u)) u = je.GetString();
                                if (creds.TryGetProperty("password", out je)) pw = je.GetString();

                                if (!string.IsNullOrWhiteSpace(h) && !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(pw))
                                {
                                    var server = h + (string.IsNullOrWhiteSpace(p) ? string.Empty : ":" + p);
                                    odbcConnStr = $"Driver={{HDBODBC}};ServerNode={server};UID={u};PWD={pw};";
                                    break;
                                }
                            }
                            if (odbcConnStr != null) break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Failed to parse VCAP_SERVICES: " + ex.Message);
                    }
                }
            }
        }

        // Use ODBC to connect to HANA
        if (!string.IsNullOrWhiteSpace(odbcConnStr))
        {
            try
            {
                using var conn = new OdbcConnection(odbcConnStr);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT \"ID\",\"name\",\"price\",\"createdAt\" FROM \"Products\" ORDER BY \"ID\"";
                var list = new List<Product>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var price = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                    var createdAt = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3);
                    list.Add(new Product(id, name, price, createdAt));
                }
                return list;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ODBC provider failed: " + ex.Message);
            }
        }

        // Nothing worked, return null (caller will fallback to mock)
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("GetProductsFromHana unexpected error: " + ex.Message);
        return null;
    }
}

public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
