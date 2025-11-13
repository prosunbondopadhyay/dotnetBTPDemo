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
        // Try to parse VCAP_SERVICES (Cloud Foundry) for HANA service
        var vcap = Environment.GetEnvironmentVariable("VCAP_SERVICES");
        if (string.IsNullOrWhiteSpace(vcap))
        {
            Console.Error.WriteLine("VCAP_SERVICES not found");
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(vcap);
            
            // Look for HANA service binding
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Check if this is a HANA service
                if (!prop.Name.Contains("hana", StringComparison.OrdinalIgnoreCase))
                    continue;
                
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) 
                    continue;

                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (!item.TryGetProperty("credentials", out var creds)) 
                        continue;

                    // Extract HANA connection info from credentials
                    string? host = null, port = null, user = null, pwd = null, schema = null;
                    
                    if (creds.TryGetProperty("host", out var je)) host = je.GetString();
                    if (creds.TryGetProperty("port", out je)) port = je.GetRawText().Trim('\"');
                    if (creds.TryGetProperty("user", out je)) user = je.GetString();
                    if (creds.TryGetProperty("password", out je)) pwd = je.GetString();
                    if (creds.TryGetProperty("schema", out je)) schema = je.GetString();

                    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pwd))
                        continue;

                    // Use HANA's SQL REST API endpoint
                    // Format: https://host:port/sap/hana/query?command=SELECT...
                    var baseUrl = $"https://{host}:{port ?? "443"}";
                    var sqlQuery = "SELECT \"ID\",\"name\",\"price\",\"createdAt\" FROM \"Products\" ORDER BY \"ID\"";

                    Console.Error.WriteLine($"Attempting HANA REST connection to {host} with user {user}");

                    try
                    {
                        using var httpClient = new HttpClientHandler();
                        // Trust self-signed certs (HANA Cloud uses these)
                        httpClient.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true;
                        
                        using var client = new HttpClient(httpClient);
                        
                        // Set basic auth header
                        var credentials = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{user}:{pwd}"));
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                        
                        // Try HDB SQL REST API endpoint
                        var restUrl = $"{baseUrl}/sap/hana/exec/query";
                        var requestBody = new { sql = sqlQuery, limit = 10000 };
                        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        
                        var response = await client.PostAsync(restUrl, content);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();
                            Console.Error.WriteLine($"HANA REST response: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}");
                            
                            // Parse results
                            var list = new List<Product>();
                            using var resultDoc = System.Text.Json.JsonDocument.Parse(responseContent);
                            
                            if (resultDoc.RootElement.TryGetProperty("result", out var resultProp) || 
                                resultDoc.RootElement.TryGetProperty("Results", out resultProp))
                            {
                                if (resultProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var row in resultProp.EnumerateArray())
                                    {
                                        if (row.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                                        var values = row.EnumerateArray().ToList();
                                        if (values.Count >= 4)
                                        {
                                            var id = values[0].TryGetInt32(out var idVal) ? idVal : 0;
                                            var name = values[1].ValueKind == System.Text.Json.JsonValueKind.String ? values[1].GetString() ?? "" : "";
                                            var price = values[2].TryGetDecimal(out var priceVal) ? priceVal : 0m;
                                            var createdAt = values[3].ValueKind == System.Text.Json.JsonValueKind.String ? DateTime.Parse(values[3].GetString() ?? DateTime.UtcNow.ToString()) : DateTime.UtcNow;
                                            
                                            list.Add(new Product(id, name, price, createdAt));
                                        }
                                    }
                                }
                            }
                            
                            if (list.Count > 0)
                            {
                                Console.Error.WriteLine($"Retrieved {list.Count} products from HANA via REST");
                                return list;
                            }
                        }
                        else
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            // Log status + body for debugging
                            Console.Error.WriteLine($"HANA REST API failed with status {(int)response.StatusCode} {response.ReasonPhrase}. Response body: {body.Substring(0, Math.Min(1000, body.Length))}");
                        }
                        
                    }
                    catch (HttpRequestException ex)
                    {
                        // Log full exception including stack trace for diagnosis
                        Console.Error.WriteLine($"HANA REST API connection failed: {ex}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse VCAP_SERVICES: {ex.Message}");
        }

        // Nothing worked, return null (caller will fallback to mock)
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"GetProductsFromHana unexpected error: {ex.Message}");
        return null;
    }
}

public record Product(int Id, string Name, decimal Price, DateTime CreatedAt);
public record CreateProductDto(string Name, decimal Price);
public record UpdateProductDto(string Name, decimal Price);
