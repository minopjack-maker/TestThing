using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

// --- App + Config ---
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// --- CORS ---
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

// --- DB ---
builder.Services.AddDbContext<VisitDbContext>(opt =>
    opt.UseSqlite("Data Source=visitortracker.db"));

var app = builder.Build();

// --- Middleware setup ---
app.UseCors("AllowNetlify");
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// --- Helper: hash or anonymize IP ---
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

// --- Ensure DB exists ---
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<VisitDbContext>().Database.EnsureCreated();

// ================================================================
// 🌍 UNIVERSAL REQUEST LOGGER (with visitor cookie)
// ================================================================
app.Use(async (ctx, next) =>
{
    var db = ctx.RequestServices.GetRequiredService<VisitDbContext>();

    // --- Determine IP (respect X-Forwarded-For if present) ---
    string? ip = ctx.Connection.RemoteIpAddress?.ToString();
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
    {
        var first = xff.ToString().Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(first))
            ip = first;
    }

    string ua = ctx.Request.Headers["User-Agent"].ToString();
    string lang = ctx.Request.Headers["Accept-Language"].ToString();
    string method = ctx.Request.Method;
    string path = ctx.Request.Path.ToString();

    // --- VISITOR COOKIE: set server-side if missing ---
    const string CookieName = "visitorId";
    string visitorId;
    if (!ctx.Request.Cookies.TryGetValue(CookieName, out visitorId) || string.IsNullOrWhiteSpace(visitorId))
    {
        visitorId = Guid.NewGuid().ToString("N"); // no-dashes
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,           // <-- set to false for local HTTP testing only
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(2)
        };
        ctx.Response.Cookies.Append(CookieName, visitorId, cookieOptions);
    }

    // --- Capture small request body text (optional) ---
    string? bodyText = null;
    if (ctx.Request.ContentLength is > 0 && ctx.Request.ContentLength < 2048)
    {
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        bodyText = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
    }

    // --- Geo lookup (best-effort) ---
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

    // --- Short hashed form of visitorId for DB (avoid storing raw visitorId) ---
    string? hashedVisitor = null;
    if (!string.IsNullOrEmpty(visitorId))
    {
        using var sha = SHA256.Create();
        hashedVisitor = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(visitorId)))[..8];
    }

    var visit = new Visit
    {
        TimestampUtc = DateTime.UtcNow,
        Ip = TruncateOrHashIp(ip, hash: true),
        Path = $"{method} {path}",
        UserAgent = ua,
        Language = lang,
        Country = country,
        City = city,
        Isp = isp,
        Extra = hashedVisitor ?? bodyText // store hashed visitorId (preferred); fallback to bodyText if needed
    };

    db.Visits.Add(visit);
    await db.SaveChangesAsync();

    Console.WriteLine($"[Log] {visit.TimestampUtc:u} {visit.Ip} {visit.Country}/{visit.City} {visit.Path} visitor={hashedVisitor}");

    await next();
});

// --- Static files (frontend) ---
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Endpoints ---
app.MapGet("/", () => Results.Text("✅ Visitor Tracker is running"));
app.MapGet("/health", () => Results.Json(new { ok = true, time = DateTime.UtcNow }));

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
    catch { }

    var visit = new Visit
    {
        TimestampUtc = DateTime.UtcNow,
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

app.MapGet("/visits", async (VisitDbContext db) =>
{
    var list = await db.Visits
        .OrderByDescending(v => v.TimestampUtc)
        .Take(100)
        .ToListAsync();

    return Results.Json(list);
});

app.MapGet("/cleanup", async (VisitDbContext db) =>
{
    var cutoff = DateTime.UtcNow.AddDays(-30);
    var old = db.Visits.Where(v => v.TimestampUtc < cutoff);
    db.Visits.RemoveRange(old);
    var deleted = await db.SaveChangesAsync();
    return Results.Json(new { deleted });
});

app.Run();

// ================================================================
// 📘 DATA CONTEXT + MODEL
// ================================================================
class VisitDbContext : DbContext
{
    public VisitDbContext(DbContextOptions<VisitDbContext> opt) : base(opt) { }
    public DbSet<Visit> Visits => Set<Visit>();
}

class Visit
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? Ip { get; set; }
    public string? Path { get; set; }
    public string? UserAgent { get; set; }
    public string? Language { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Isp { get; set; }
    public string? Extra { get; set; }   // <- hashed visitorId or request body
}
