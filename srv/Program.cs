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

// Diagnostic helper: try multiple REST endpoints and return concise results
async Task<object> DiagnoseHanaFromApp()
{
    var outResults = new List<object>();

    try
    {
        var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
        if (string.IsNullOrWhiteSpace(vcap))
        {
            return new { error = "VCAP_SERVICES not found" };
        }

        using var doc = System.Text.Json.JsonDocument.Parse(vcap);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Contains("hana", StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var item in prop.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("credentials", out var creds)) continue;

                creds.TryGetProperty("host", out var jhost);
                creds.TryGetProperty("port", out var jport);
                creds.TryGetProperty("user", out var juser);
                creds.TryGetProperty("password", out var jpwd);

                var host = jhost.GetString();
                var port = jport.GetRawText().Trim('\"');
                var user = juser.GetString();
                var pwd = jpwd.GetString();

                if (string.IsNullOrWhiteSpace(host)) continue;

                var baseUrl = $"https://{host}:{(string.IsNullOrWhiteSpace(port) ? "443" : port)}";

                // endpoints to try
                var endpoints = new[]
                {
                    "/sap/hana/exec/query",
                    "/sap/hana/sql",
                    "/sap/hana/odata/v4/",
                    "/sap/hana/query",
                };

                using var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                var cred = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{user}:{pwd}"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred);

                var sqlBody = System.Text.Json.JsonSerializer.Serialize(new { sql = "SELECT \"ID\",\"name\",\"price\",\"createdAt\" FROM \"Products\" ORDER BY \"ID\"", limit = 10000 });
                var content = new StringContent(sqlBody, System.Text.Encoding.UTF8, "application/json");

                // First, a plain GET to base root
                try
                {
                    var r = await client.GetAsync(baseUrl);
                    var body = await SafeRead(r.Content);
                    outResults.Add(new { endpoint = baseUrl, method = "GET", status = (int)r.StatusCode, reason = r.ReasonPhrase, body = Truncate(body, 800) });
                }
                catch (Exception ex)
                {
                    outResults.Add(new { endpoint = baseUrl, method = "GET", error = ex.Message });
                }

                // Try POSTs to each candidate endpoint
                foreach (var ep in endpoints)
                {
                    var url = baseUrl.TrimEnd('/') + ep;
                    try
                    {
                        var r = await client.PostAsync(url, content);
                        var body = await SafeRead(r.Content);
                        outResults.Add(new { endpoint = url, method = "POST", status = (int)r.StatusCode, reason = r.ReasonPhrase, body = Truncate(body, 1200) });
                    }
                    catch (Exception ex)
                    {
                        outResults.Add(new { endpoint = url, method = "POST", error = ex.Message });
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        return new { error = ex.Message };
    }

    return new { results = outResults };

    static string Truncate(string? s, int len) => string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s.Substring(0, len));
    static async Task<string> SafeRead(HttpContent? c)
    {
        try
        {
            if (c == null) return string.Empty;
            return await c.ReadAsStringAsync();
        }
        catch
        {
            return string.Empty;
        }
    }
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

// Diagnostic endpoint: run several HANA REST attempts from inside the app
app.MapGet("/api/diagnose-hana", async () =>
{
    var results = await DiagnoseHanaFromApp();
    return Results.Ok(results);
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
// Helper: try to read HANA credentials and query Products table via HTTP REST.
async Task<List<Product>?> GetProductsFromHana()
{
    try
    {
        var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
        if (string.IsNullOrWhiteSpace(vcap))
        {
            Console.Error.WriteLine("VCAP_SERVICES not found");
            return null;
        }

        using var doc = JsonDocument.Parse(vcap);

        // Look for any service group whose key contains "hana"
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

                string? host = null, port = null, user = null, pwd = null;
                if (creds.TryGetProperty("host", out var je)) host = je.GetString();
                if (creds.TryGetProperty("port", out je))  port = je.ValueKind == JsonValueKind.Number
                                                                ? je.GetRawText()
                                                                : je.GetString();
                if (creds.TryGetProperty("user", out je)) user = je.GetString();
                if (creds.TryGetProperty("password", out je)) pwd = je.GetString();

                if (string.IsNullOrWhiteSpace(host) ||
                    string.IsNullOrWhiteSpace(user) ||
                    string.IsNullOrWhiteSpace(pwd))
                    continue;

                var serverNode = string.IsNullOrWhiteSpace(port)
                    ? $"{host}:443"
                    : $"{host}:{port!.Trim('\"')}";

                // Build HANA ODBC connection string
                var csb = new OdbcConnectionStringBuilder
                {
                    ["Driver"] = "HDBODBC",        // on Linux HANA client, usually HDBODBC
                    ["ServerNode"] = serverNode,
                    ["UID"] = user,
                    ["PWD"] = pwd,
                    // Optional: if you run into TLS issues, you can experiment with:
                    // ["Encrypt"] = "true",
                    // ["ValidateCertificate"] = "false"
                };

                var connStr = csb.ConnectionString;
                Console.Error.WriteLine($"Connecting to HANA via ODBC at {serverNode} as {user}");

                var products = new List<Product>();

                using (var conn = new OdbcConnection(connStr))
                {
                    await conn.OpenAsync();

                    const string sql =
                        "SELECT \"ID\",\"name\",\"price\",\"createdAt\" " +
                        "FROM \"Products\" ORDER BY \"ID\"";

                    using var cmd = new OdbcCommand(sql, conn);
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

                if (products.Count > 0)
                {
                    Console.Error.WriteLine($"Retrieved {products.Count} products from HANA via ODBC");
                    return products;
                }
            }
        }

        // No usable HANA binding found
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"GetProductsFromHana failed: {ex}");
        return null;
    }
}


public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
