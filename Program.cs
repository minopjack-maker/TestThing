using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;



app.UseCors("AllowNetlify");
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// serve static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();


var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddCors(o =>
{
    o.AddPolicy("AllowNetlify", p =>
        p.WithOrigins(
            "https://your-netlify-site.netlify.app",
            "https://yourcustomdomain.com"
        )
        .AllowAnyMethod()
        .AllowAnyHeader());
});

// SQLite DB
builder.Services.AddDbContext<VisitDbContext>(opt =>
    opt.UseSqlite("Data Source=visitortracker.db"));

var app = builder.Build();
app.UseCors("AllowNetlify");

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<VisitDbContext>().Database.EnsureCreated();

static string TruncateOrHashIp(string? ip, bool hash = true)
{
    if (string.IsNullOrEmpty(ip)) return "";
    if (!hash && ip.Contains('.'))
    {
        var parts = ip.Split('.');
        if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.*.*";
    }
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(ip)))[..8];
}

// --- Root ---
app.MapGet("/", () => Results.Text("✅ Visitor Tracker is running"));

// --- Health check ---
app.MapGet("/health", () => Results.Json(new { ok = true, time = DateTime.UtcNow }));

// --- Log visit ---
app.MapPost("/track-visit", async (HttpContext ctx, VisitDbContext db) =>
{
    string? ip = ctx.Connection.RemoteIpAddress?.ToString();
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
    {
        var first = xff.ToString().Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(first)) ip = first;
    }

    string ua = ctx.Request.Headers["User-Agent"].ToString();
    string lang = ctx.Request.Headers["Accept-Language"].ToString();
    string? country = null, city = null, isp = null;

    try
    {
        using var http = new HttpClient();
        var geo = await http.GetFromJsonAsync<dynamic>($"https://ipapi.co/{ip}/json/");
        country = (string?)geo?.country_name;
        city = (string?)geo?.city;
        isp = (string?)geo?.org;
    }
    catch { /* ignore if offline or rate-limited */ }

    var visit = new Visit
    {
        Ip = TruncateOrHashIp(ip, hash: true),
        Path = ctx.Request.Path,
        UserAgent = ua,
        Language = lang,
        Country = country,
        City = city,
        Isp = isp
    };

    db.Visits.Add(visit);
    await db.SaveChangesAsync();

    Console.WriteLine($"[Visitor] {visit.Ip} {visit.Country}/{visit.City} {visit.UserAgent}");
    return Results.NoContent();
});

// --- List visits ---
app.MapGet("/visits", async (VisitDbContext db) =>
{
    var list = await db.Visits
        .OrderByDescending(v => v.TimestampUtc)
        .Take(50)
        .ToListAsync();

    return Results.Json(list);
});

// --- Cleanup old records (30 days) ---
app.MapGet("/cleanup", async (VisitDbContext db) =>
{
    var cutoff = DateTime.UtcNow.AddDays(-30);
    var old = db.Visits.Where(v => v.TimestampUtc < cutoff);
    db.Visits.RemoveRange(old);
    var deleted = await db.SaveChangesAsync();
    return Results.Json(new { deleted });
});

app.Run();
