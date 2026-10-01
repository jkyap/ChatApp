using ChatApp.Web.Auth;
using ChatApp.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Data folder: env CHATAPP_DATA_DIR (Docker sets /data), else ./appdata
var dataDir = Environment.GetEnvironmentVariable("CHATAPP_DATA_DIR")
              ?? Path.Combine(builder.Environment.ContentRootPath, "appdata");
Directory.CreateDirectory(dataDir);

var db = new ChatDb(Path.Combine(dataDir, "chat.db"));
db.Initialize();

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(new TokenProtector(Path.Combine(dataDir, "auth.key")));

var app = builder.Build();

app.UseDefaultFiles();
// no-cache so browser reloads always pick up updated app.js/styles.css
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

AuthEndpoints.Map(app);

app.Run();
