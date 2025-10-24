public class Visit
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
}
