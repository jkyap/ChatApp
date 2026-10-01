using ChatApp.Web.Data;

namespace ChatApp.Web.Auth;

/// <summary>
/// The HTTP API. Requests authenticate via "Authorization: Bearer" header
/// (per-tab, so one browser can hold two users); a cookie copy exists only
/// to bootstrap a fresh tab after refresh.
/// </summary>
public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/register", (RegisterModel model, ChatDb db, TokenProtector tokens, HttpContext http) =>
        {
            if (!CsrfOk(http)) return Results.StatusCode(403);
            var (user, reason) = db.CreateUser(model.Username ?? "", model.Password ?? "");
            if (user is null) return Results.BadRequest(new { error = reason });

            var token = tokens.Issue(user.Id);
            SetCookie(http, token);
            return Results.Ok(new { token, user });
        });

        api.MapPost("/login", (RegisterModel model, ChatDb db, TokenProtector tokens, HttpContext http) =>
        {
            if (!CsrfOk(http)) return Results.StatusCode(403);
            var user = db.VerifyLogin(model.Username ?? "", model.Password ?? "");
            if (user is null) return Results.Unauthorized();

            var token = tokens.Issue(user.Id);
            SetCookie(http, token);
            return Results.Ok(new { token, user });
        });

        api.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete("chat_session");
            return Results.NoContent();
        });

        api.MapGet("/me", (TokenProtector tokens, HttpContext http, ChatDb db) =>
        {
            var userId = Resolve(tokens, http);
            if (userId is null) return Results.Unauthorized();
            var user = db.GetUser(userId.Value);
            return user is null ? Results.Unauthorized() : Results.Ok(new { user });
        });

        // Find a user by name (needed to start a chat). Confirms usernames
        // exist; add throttling before exposing publicly.
        api.MapGet("/users/lookup", (string? username, TokenProtector tokens, HttpContext http, ChatDb db) =>
        {
            if (Resolve(tokens, http) is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(username)) return Results.BadRequest();
            var user = db.FindByUsername(username.Trim());
            return user is null ? Results.NotFound() : Results.Ok(new { id = user.Id, username = user.Username });
        });

        api.MapGet("/conversations", (TokenProtector tokens, HttpContext http, ChatDb db) =>
        {
            var me = Resolve(tokens, http);
            if (me is null) return Results.Unauthorized();

            var rows = db.ListConversations(me.Value);
            var cache = new Dictionary<int, UserInfo?>();
            var items = rows.Select(r =>
            {
                var otherId = (int)PeerId(r.ConversationId, me.Value);
                if (!cache.TryGetValue(otherId, out var other)) cache[otherId] = other = db.GetUser(otherId);
                return new
                {
                    peer = new { id = otherId, username = other?.Username ?? $"user{otherId}" },
                    lastSeq = r.Seq,
                    lastFrom = r.SenderId,
                    lastText = r.Text.Length > 80 ? r.Text[..80] + "…" : r.Text,
                    lastAt = r.CreatedAtUnixMs,
                };
            }).ToList();
            return Results.Ok(new { conversations = items });
        });

        api.MapGet("/messages", (int? peer, int? since, int? take, TokenProtector tokens, HttpContext http, ChatDb db) =>
        {
            var me = Resolve(tokens, http);
            if (me is null || peer is null || peer == me) return Results.Unauthorized();

            // A conversation is just (me, other): you can read your own side
            // with anyone (no contact requests yet).
            var sinceSeq = Math.Max(0, since ?? 0);
            var capped = Math.Clamp(take ?? 50, 1, 100); // limit page size
            var page = db.GetConversation(me.Value, peer.Value, sinceSeq, capped);
            var names = new Dictionary<int, string>();
            foreach (var m in page.Messages)
            {
                if (!names.ContainsKey(m.SenderId)) names[m.SenderId] = db.GetUser(m.SenderId)?.Username ?? "?";
            }
            return Results.Ok(new
            {
                latestSeq = page.LatestSeq,
                messages = page.Messages.Select(m => new
                {
                    id = m.Id,
                    seq = m.Seq,
                    from = m.SenderId,
                    fromName = names[m.SenderId],
                    to = m.RecipientId,
                    text = m.Text,
                    at = m.CreatedAtUtc.ToUnixTimeMilliseconds(),
                }),
            });
        });

        // Sending. Retries are safe: the DB refuses to store the same
        // clientMessageId twice and just returns the original message.
        api.MapPost("/messages", (SendModel model, TokenProtector tokens, HttpContext http, ChatDb db) =>
        {
            var me = Resolve(tokens, http);
            if (me is null || model.To <= 0 || model.To == me) return Results.Unauthorized();

            var outcome = db.SendMessage(me.Value, model.To, model.ClientMessageId, model.Text ?? "");
            if (outcome.Message is null) return Results.Json(new { error = outcome.Reason, status = outcome.Status }, statusCode: 422);

            var m = outcome.Message;
            return Results.Ok(new { status = outcome.Status, message = new { id = m.Id, seq = m.Seq } });
        });
    }

    private static long PeerId(long conversationId, int me)
    {
        long lo = conversationId / 100_000_000L, hi = conversationId % 100_000_000L;
        return lo == me ? hi : lo;
    }

    private static int? Resolve(TokenProtector tokens, HttpContext http)
    {
        // Header before cookie: all windows of one browser share the cookie,
        // but each window sends its own header - that keeps two logged-in
        // users in one browser separate.
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var id = tokens.Validate(header[7..]);
            if (id is not null) return id;
        }
        if (http.Request.Cookies.TryGetValue("chat_session", out var cookie))
            return tokens.Validate(cookie);
        return null;
    }

    /// <summary>Belt-and-braces CSRF check: the request must come from our own page.</summary>
    private static bool CsrfOk(HttpContext http)
    {
        var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length == 0 || site is "same-origin" or "none";
    }

    private static void SetCookie(HttpContext http, string token)
    {
        http.Response.Cookies.Append("chat_session", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(7),
            Path = "/",
        });
    }

    public sealed record RegisterModel(string? Username, string? Password);
    public sealed record SendModel(int To, string? Text, string? ClientMessageId);
}
