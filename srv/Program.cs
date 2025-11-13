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

// Helper: try to read HANA credentials and query Products table via ODBC
async Task<List<Product>?> GetProductsFromHana()
{
    try
    {
        var connStr = GetHanaOdbcConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return null;

        using var conn = new OdbcConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"ID\", \"name\", \"price\", \"createdAt\" FROM \"Products\" ORDER BY \"ID\"";
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
        Console.Error.WriteLine("HANA read failed: " + ex.Message);
        return null;
    }
}

string? GetHanaOdbcConnectionString()
{
    // If a full connection string is provided explicitly, use it
    var explicit = Environment.GetEnvironmentVariable("HANA_CONNECTION");
    if (!string.IsNullOrWhiteSpace(explicit)) return explicit;

    // Try common individual env vars
    var host = Environment.GetEnvironmentVariable("HANA_HOST");
    var port = Environment.GetEnvironmentVariable("HANA_PORT");
    var user = Environment.GetEnvironmentVariable("HANA_USER");
    var pwd = Environment.GetEnvironmentVariable("HANA_PASSWORD");
    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pwd))
    {
        var server = host + (string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port);
        // HDBODBC driver connection string
        return $"Driver={{HDBODBC}};ServerNode={server};UID={user};PWD={pwd};";
    }

    // Try to parse VCAP_SERVICES (Cloud Foundry) for hana or hdi service
    var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
    if (string.IsNullOrWhiteSpace(vcap)) return null;
    try
    {
        var doc = JsonNode.Parse(vcap);
        if (doc is JsonObject obj)
        {
            foreach (var prop in obj)
            {
                var arr = prop.Value as JsonArray;
                if (arr == null) continue;
                foreach (var item in arr)
                {
                    var service = item as JsonObject;
                    if (service == null) continue;
                    var label = service["label"]?.ToString() ?? string.Empty;
                    if (!label.ToLower().Contains("hana") && !label.ToLower().Contains("hdi")) continue;
                    var credentials = service["credentials"] as JsonObject;
                    if (credentials == null) continue;
                    var h = credentials["host"]?.ToString() ?? credentials["hostname"]?.ToString();
                    var p = credentials["port"]?.ToString();
                    var u = credentials["user"]?.ToString() ?? credentials["username"]?.ToString();
                    var pw = credentials["password"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(h) && !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(pw))
                    {
                        var server = h + (string.IsNullOrWhiteSpace(p) ? string.Empty : ":" + p);
                        return $"Driver={{HDBODBC}};ServerNode={server};UID={u};PWD={pw};";
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Failed to parse VCAP_SERVICES: " + ex.Message);
    }

    return null;
}

public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
