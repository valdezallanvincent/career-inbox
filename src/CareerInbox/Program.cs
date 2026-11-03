using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("CAREER_PORT"), out var customPort) ? customPort : 5680;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262144);
builder.Services.AddSingleton<Inbox>();
var app = builder.Build();
app.Use(async (context, next) => {
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1")) { context.Response.StatusCode = 403; return; }
    // Local service: reject browser cross-origin mutations and non-JSON posts.
    if (context.Request.Method == "POST" && (context.Request.Headers.ContainsKey("Origin") || !context.Request.HasJsonContentType())) {
        context.Response.StatusCode = 403; return;
    }
    try { await next(context); }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
});
app.MapGet("/health", () => new { status = "ok", service = "Career Inbox", version = "1.0.0" });
app.MapPost("/jobs", (JobInput input, Inbox inbox) => Results.Ok(inbox.Capture(input)));
app.MapGet("/jobs", (Inbox inbox) => Results.Ok(inbox.List()));
app.MapPost("/status", (StatusInput input, Inbox inbox) => Results.Ok(inbox.Update(input)));
app.MapPost("/digest", (Inbox inbox) => Results.Ok(inbox.Digest()));
app.MapGet("/digest", (Inbox inbox) => Results.Text(inbox.Report(), "text/markdown; charset=utf-8"));
app.Run();

public record JobInput(string? Url, string? Title, string? Company, string? Description,
    decimal? HourlyUsd, string? Eligibility, string? VacancyStatus);
public record StatusInput(long Id, string? Status, string? Note);
public record Job(long Id, string Url, string Title, string Company, string Description, decimal? HourlyUsd,
    string Eligibility, string VacancyStatus, string Status, int Score, string Reasons, string CreatedAt, string UpdatedAt);

public sealed class Inbox {
    readonly string root;
    readonly string connectionString;
    readonly object gate = new();
    readonly Dictionary<string, int> skills = new() {
        [@"\b(c#|csharp|\.net|asp\.net)\b|(?<!\w)\.net\b|\bc#"] = 25,
        [@"\bazure\b"] = 15, [@"\b(sql|sql server|postgresql)\b"] = 15,
        [@"\b(angular|react|typescript)\b"] = 10,
        [@"\b(api|apis|integration|integrations|webhook|webhooks)\b"] = 15,
        [@"\b(n8n|automation|llm|rag|ai)\b"] = 10,
        [@"\b(docker|ci/cd|devops)\b"] = 10
    };
    public Inbox() : this(Environment.GetEnvironmentVariable("CAREER_INBOX_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data")) { }
    public Inbox(string directory) {
        root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "inbox.db"), DefaultTimeout = 10 }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS jobs (
          id INTEGER PRIMARY KEY, url TEXT NOT NULL UNIQUE, title TEXT NOT NULL, company TEXT NOT NULL,
          description TEXT NOT NULL, hourly_usd REAL, eligibility TEXT NOT NULL, vacancy_status TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'new', score INTEGER NOT NULL, reasons TEXT NOT NULL,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS events (
          id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL, event TEXT NOT NULL, note TEXT NOT NULL, at TEXT NOT NULL);
        PRAGMA user_version=1;
        """; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    static string Required(string? value, string name, int max) {
        var text = (value ?? "").Trim();
        if (text.Length == 0 || text.Length > max) throw new ArgumentException($"{name} is required and must be at most {max} characters.");
        return text;
    }
    public static string CanonicalUrl(string? value) {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Provide a valid public http(s) job URL without credentials.");
        if (uri.IsLoopback || !uri.Host.Contains('.')) throw new ArgumentException("Use the original public job listing URL.");
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !Regex.IsMatch(x.Split('=')[0], @"^(utm_.*|fbclid|gclid|ref|source)$", RegexOptions.IgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new UriBuilder(uri) { Fragment = "", Query = string.Join('&', parts), Path = uri.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri;
    }
    public object Capture(JobInput input) {
        var url = CanonicalUrl(input.Url); var title = Required(input.Title,"Title",300);
        var company = Required(input.Company,"Company",200); var description = Required(input.Description,"Description",30000);
        if (input.HourlyUsd is < 0 or > 10000) throw new ArgumentException("Hourly USD must be between 0 and 10000, or omitted if unknown.");
        var eligibility = input.Eligibility?.Trim().ToLowerInvariant() ?? "unknown";
        var vacancy = input.VacancyStatus?.Trim().ToLowerInvariant() ?? "unverified";
        if (!new[] {"yes","no","unknown"}.Contains(eligibility)) throw new ArgumentException("Eligibility must be yes, no, or unknown.");
        if (!new[] {"open","closed","unverified"}.Contains(vacancy)) throw new ArgumentException("Vacancy status must be open, closed, or unverified.");
        var text = title + " " + description;
        if (Regex.IsMatch(text,@"this job has been closed|no longer accepting applications|position has been filled",RegexOptions.IgnoreCase)) vacancy = "closed";
        var labels = new[] {".NET/C#","Azure","SQL","Frontend","APIs/integrations","AI/automation","Deployment"};
        var matches = skills.Select((s,i) => new { Label=labels[i], Pattern=s.Key, Weight=s.Value })
            .Where(s => Regex.IsMatch(text,s.Pattern,RegexOptions.IgnoreCase)).ToList();
        var score = matches.Sum(s => s.Weight);
        var reasons = new List<string> { $"Technical keyword match: {score}/100 (not a hiring probability)." };
        reasons.Add("Matched: " + (matches.Count == 0 ? "none." : string.Join(", ",matches.Select(s=>$"{s.Label} +{s.Weight}"))+"."));
        var min = decimal.TryParse(Environment.GetEnvironmentVariable("CAREER_MIN_HOURLY_USD"), out var configured) && configured >= 0 ? configured : 10m;
        if (eligibility == "unknown") reasons.Add("Philippines eligibility needs verification.");
        if (vacancy == "unverified") reasons.Add("Vacancy status is unverified; inspect the original listing.");
        if (input.HourlyUsd == null) reasons.Add("Compensation is unknown; confirm rate and currency.");
        var blocked = vacancy == "closed" || eligibility == "no" || input.HourlyUsd < min;
        if (blocked) reasons.Add("Excluded from shortlist: closed, ineligible, or below minimum hourly rate.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        lock (gate) {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO jobs(url,title,company,description,hourly_usd,eligibility,vacancy_status,status,score,reasons,created_at,updated_at)
            VALUES($url,$title,$company,$description,$rate,$eligibility,$vacancy,$status,$score,$reasons,$now,$now)
            ON CONFLICT(url) DO NOTHING;
            """;
            foreach (var pair in new Dictionary<string,object?> { ["url"]=url,["title"]=title,["company"]=company,["description"]=description,
                ["rate"]=input.HourlyUsd,["eligibility"]=eligibility,["vacancy"]=vacancy,["status"]=blocked?"excluded":"new",
                ["score"]=score,["reasons"]=string.Join(" ",reasons),["now"]=now }) cmd.Parameters.AddWithValue("$"+pair.Key,pair.Value??DBNull.Value);
            var added = cmd.ExecuteNonQuery() == 1;
            cmd.CommandText = "SELECT id FROM jobs WHERE url=$url"; var id = Convert.ToInt64(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT score FROM jobs WHERE url=$url"; score = Convert.ToInt32(cmd.ExecuteScalar());
            if (added) Event(db,tx,id,"captured","Saved and scored using explicit rules.");
            tx.Commit();
            return new { id, duplicate = !added, score, message = added ? $"Saved job #{id}. Match score {score}/100. {string.Join(" ",reasons)}" : $"Already saved as job #{id}; no duplicate row created. Original details were preserved." };
        }
    }
    static void Event(SqliteConnection db, SqliteTransaction tx, long id, string name, string note) {
        using var cmd=db.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO events(job_id,event,note,at) VALUES($id,$event,$note,$at)";
        cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$event",name); cmd.Parameters.AddWithValue("$note",note);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public object Update(StatusInput input) {
        var status=input.Status?.Trim().ToLowerInvariant();
        if (!new[]{"new","shortlisted","applied","interview","rejected","withdrawn","closed","excluded"}.Contains(status)) throw new ArgumentException("Unknown application status.");
        if ((input.Note??"").Length>2000) throw new ArgumentException("Note must be at most 2000 characters.");
        lock(gate) {
            using var db=Open(); using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand(); cmd.Transaction=tx;
            cmd.CommandText="UPDATE jobs SET status=$status,updated_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$status",status); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id",input.Id);
            if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("Job ID does not exist.");
            Event(db,tx,input.Id,"status:"+status,input.Note??""); tx.Commit();
        }
        return new {message=$"Job #{input.Id} is now {status}. This records your update; no application or message was sent."};
    }
    public List<Job> List() {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT * FROM jobs ORDER BY score DESC,id DESC";
        using var r=cmd.ExecuteReader(); var rows=new List<Job>();
        while(r.Read()) rows.Add(new Job(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetDecimal(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetInt32(9),r.GetString(10),r.GetString(11),r.GetString(12)));
        return rows;
    }
    static string Safe(string value) => value.Replace("\r"," ").Replace("\n"," ").Replace("|","/").Replace("<","&lt;").Replace(">","&gt;").Replace("[","\\[").Replace("]","\\]");
    public string Report() {
        var jobs=List(); var now=DateTimeOffset.UtcNow;
        var min = decimal.TryParse(Environment.GetEnvironmentVariable("CAREER_MIN_HOURLY_USD"), out var configured) && configured >= 0 ? configured : 10m;
        var candidates=jobs.Where(j=>(j.Status is "new" or "shortlisted") && j.VacancyStatus != "closed" && j.Eligibility != "no" && (j.HourlyUsd == null || j.HourlyUsd >= min)).ToList();
        var sb=new StringBuilder("# Personal career shortlist\n\nGenerated: "+now.ToString("u")+"\n\nRule-based ranking, not AI or hiring probability. Source status is user-entered, not independently verified.\n\n");
        sb.AppendLine("| ID | Role | Company | Score | Hourly USD | Vacancy | PH eligible |\n|---|---|---|---:|---:|---|---|");
        foreach(var j in candidates.Take(10)) sb.AppendLine($"| {j.Id} | {Safe(j.Title)} | {Safe(j.Company)} | {j.Score} | {j.HourlyUsd?.ToString()??"Unknown"} | {j.VacancyStatus} | {j.Eligibility} |");
        foreach(var j in candidates.Take(10)) sb.AppendLine($"\n## #{j.Id}: {Safe(j.Title)}\n\n{Safe(j.Url)}\n\n{j.Reasons}"+(now-DateTimeOffset.Parse(j.CreatedAt)>TimeSpan.FromDays(14)?"\n\nSTALE: captured over 14 days ago; recheck the vacancy.":""));
        sb.AppendLine("\n## Follow-ups due (manual review)\n");
        foreach(var j in jobs.Where(j=>j.Status=="applied" && now-DateTimeOffset.Parse(j.UpdatedAt)>=TimeSpan.FromDays(7))) sb.AppendLine($"- #{j.Id}: {Safe(j.Company)} — {Safe(j.Title)}. Consider a follow-up; none was sent.");
        sb.AppendLine($"\nTracked: {jobs.Count}. Shortlist candidates: {candidates.Count}.\n\nCapture or update jobs using the local n8n forms. Keep this report private.");
        return sb.ToString();
    }
    public object Digest() {
        lock(gate) { var report=Report(); var path=Path.Combine(root,"latest-shortlist.md"); var temp=path+".tmp";
            File.WriteAllText(temp,report); File.Move(temp,path,true);
            return new {message="Daily shortlist refreshed. Open http://127.0.0.1:5680/digest or data/latest-shortlist.md.",report}; }
    }
}
