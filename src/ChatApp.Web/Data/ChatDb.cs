using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChatApp.Web.Auth;

namespace ChatApp.Web.Data;

public sealed record UserInfo(int Id, string Username);

public sealed record Message(
    long Id,
    long ConversationId,
    int Seq,
    int SenderId,
    int RecipientId,
    string Text,
    DateTimeOffset CreatedAtUtc);

public sealed record ConversationRow(long ConversationId, long MessageId, int Seq, int SenderId, string Text, long CreatedAtUnixMs);

/// <summary>
/// All storage in one SQLite file. One server writes at a time, so "last
/// message number + 1" is safe without extra locks. Multi-server needs a
/// shared store; the unique (conversation, number) index at least turns a
/// collision into a catchable error. See README.
/// </summary>
public sealed class ChatDb
{
    private readonly string _connectionString;

    public ChatDb(string databasePath)
    {
        _connectionString = $"Data Source={databasePath};Cache=Shared";
    }

    public void Initialize()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY,
                username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                pass_salt     BLOB NOT NULL,
                pass_hash     BLOB NOT NULL,   -- one-way scramble; the password itself is never stored
                created_at_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                id               INTEGER PRIMARY KEY,
                conversation_id  INTEGER NOT NULL,   -- one number shared by both people, see ConversationId()
                seq              INTEGER NOT NULL,   -- message number within the conversation, 1, 2, 3...
                sender_id        INTEGER NOT NULL,
                recipient_id     INTEGER NOT NULL,
                text             TEXT NOT NULL,
                created_at_ms    INTEGER NOT NULL,
                client_message_id TEXT NULL          -- browser-made id that makes retries harmless
            );

            -- a sent message can only be stored once, no matter how often it's retried
            CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_idempotency
                ON messages(sender_id, client_message_id)
                WHERE client_message_id IS NOT NULL;

            -- ...and no two messages in a conversation can share a number.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_conversation_seq
                ON messages(conversation_id, seq);
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        var con = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    /// <summary>
    /// Creates a user. Passwords are stored salted + scrambled (PBKDF2), never
    /// as text. The DB file itself is NOT encrypted - see README.
    /// </summary>
    public (UserInfo? User, string Reason) CreateUser(string username, string password)
    {
        username = username.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9._-]{3,20}$"))
            return (null, "Username must be 3-20 chars: letters, digits, dot, underscore, dash.");
        if (password.Length is < 8 or > 128)
            return (null, "Password must be 8-128 characters.");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

        using var con = Open();
        using var check = con.CreateCommand();
        check.CommandText = "SELECT 1 FROM users WHERE username = $u COLLATE NOCASE";
        check.Parameters.AddWithValue("$u", username);
        if (check.ExecuteScalar() is not null)
            return (null, "Username already taken.");

        using var ins = con.CreateCommand();
        ins.CommandText = """
            INSERT INTO users (username, pass_salt, pass_hash, created_at_ms)
            VALUES ($u, $salt, $hash, $now)
            """;
        ins.Parameters.AddWithValue("$u", username);
        ins.Parameters.AddWithValue("$salt", salt);
        ins.Parameters.AddWithValue("$hash", hash);
        ins.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var id = (int)ins.ExecuteNonQuery() > 0
            ? Convert.ToInt32((long)Scalar(con, "SELECT last_insert_rowid()")!)
            : 0;

        return (new UserInfo(id, username), "ok");
    }

    public UserInfo? VerifyLogin(string username, string password)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, username, pass_salt, pass_hash FROM users WHERE username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var id = reader.GetInt32(0);
        var name = reader.GetString(1);
        var salt = (byte[])reader[2];
        var hash = (byte[])reader[3];
        reader.Close();

        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(candidate, hash) ? new UserInfo(id, name) : null;
    }

    public UserInfo? GetUser(int id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, username FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new UserInfo(reader.GetInt32(0), reader.GetString(1)) : null;
    }

    public UserInfo? FindByUsername(string username)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, username FROM users WHERE username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new UserInfo(reader.GetInt32(0), reader.GetString(1)) : null;
    }

    public sealed record Page(List<Message> Messages, int LatestSeq);

    /// <summary>
    /// One page of a conversation:
    /// - since &gt; 0: everything newer than that number (normal poll)
    /// - otherwise: the newest `take` messages (opening a chat stays fast
    ///   even for 10k messages)
    /// </summary>
    public Page GetConversation(int me, int peer, int since, int take = 50)
    {
        var conv = ConversationId(me, peer);
        var messages = new List<Message>();
        using var con = Open();

        var latest = Convert.ToInt32((long)Scalar(con, "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE conversation_id = $c", ("$c", conv))!);
        // A bookmark past the end (e.g. browser remembers an older, bigger DB)
        // must not lock the chat out forever: fall back to the newest page.
        var newestFirst = since <= 0 || since > latest;
        using (var cmd = con.CreateCommand())
        {
            if (newestFirst)
            {
                cmd.CommandText = """
                    SELECT id, conversation_id, seq, sender_id, recipient_id, text, created_at_ms
                    FROM messages WHERE conversation_id = $c
                    ORDER BY seq DESC LIMIT $take
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, conversation_id, seq, sender_id, recipient_id, text, created_at_ms
                    FROM messages WHERE conversation_id = $c AND seq > $since
                    ORDER BY seq LIMIT $take
                    """;
                cmd.Parameters.AddWithValue("$since", since);
            }
            cmd.Parameters.AddWithValue("$c", conv);
            cmd.Parameters.AddWithValue("$take", take);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new Message(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
            }
        }
        if (newestFirst) messages.Reverse();

        // Anything saved between these two reads is picked up next poll.
        latest = Convert.ToInt32((long)Scalar(con, "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE conversation_id = $c", ("$c", conv))!);
        return new Page(messages, latest);
    }

    /// <summary>Peer list = distinct peers with a last-message preview, newest first.</summary>
    public List<ConversationRow> ListConversations(int me)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            WITH mine AS (SELECT conversation_id, MAX(seq) AS s FROM messages WHERE sender_id = $me OR recipient_id = $me GROUP BY conversation_id)
            SELECT m.conversation_id, m.id, m.seq, m.sender_id, m.text, m.created_at_ms
            FROM mine JOIN messages m ON m.conversation_id = mine.conversation_id AND m.seq = mine.s
            ORDER BY m.id DESC LIMIT 100
            """;
        cmd.Parameters.AddWithValue("$me", me);
        var rows = new List<ConversationRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ConversationRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5)));
        }
        return rows;
    }

    public sealed record SendOutcome(Message? Message, string Status, string? Reason);

    /// <summary>
    /// Saves one message. Retries are safe: the browser reuses the same
    /// client_message_id, and the unique index returns the already-saved row
    /// instead of duplicating. Number assignment happens in one transaction.
    /// Status: "created" | "duplicate" | error code.
    /// </summary>
    public SendOutcome SendMessage(int sender, int recipient, string? clientMessageId, string text)
    {
        if (clientMessageId is not null)
        {
            using var probe = Open();
            using var dup = probe.CreateCommand();
            dup.CommandText = """
                SELECT id, conversation_id, seq, sender_id, recipient_id, text, created_at_ms
                FROM messages WHERE sender_id = $s AND client_message_id = $cid
                """;
            dup.Parameters.AddWithValue("$s", sender);
            dup.Parameters.AddWithValue("$cid", clientMessageId);
            using var reader = dup.ExecuteReader();
            if (reader.Read())
            {
                var m = new Message(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
                return new SendOutcome(m, "duplicate", null);
            }
        }

        using var con = Open();
        using var tx = con.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        try
        {
            using var seqCmd = con.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM messages WHERE conversation_id = $c";
            seqCmd.Parameters.AddWithValue("$c", ConversationId(sender, recipient));
            var seq = (int)(long)seqCmd.ExecuteScalar()!;

            var now = DateTimeOffset.UtcNow;
            using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO messages (conversation_id, seq, sender_id, recipient_id, text, created_at_ms, client_message_id)
                VALUES ($c, $seq, $from, $to, $text, $now, $cid)
                """;
            ins.Parameters.AddWithValue("$c", ConversationId(sender, recipient));
            ins.Parameters.AddWithValue("$seq", seq);
            ins.Parameters.AddWithValue("$from", sender);
            ins.Parameters.AddWithValue("$to", recipient);
            ins.Parameters.AddWithValue("$text", text);
            ins.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            ins.Parameters.AddWithValue("$cid", (object?)clientMessageId ?? DBNull.Value);

            long id;
            try
            {
                ins.ExecuteNonQuery();
                id = (long)Scalar(con, "SELECT last_insert_rowid()", transaction: tx)!;
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19)
            {
                // a parallel retry saved it first; return that row
                tx.Rollback();
                if (clientMessageId is not null)
                {
                    using var after = Open();
                    using var again = after.CreateCommand();
                    again.CommandText = """
                        SELECT id, conversation_id, seq, sender_id, recipient_id, text, created_at_ms
                        FROM messages WHERE sender_id = $s AND client_message_id = $cid
                        """;
                    again.Parameters.AddWithValue("$s", sender);
                    again.Parameters.AddWithValue("$cid", clientMessageId);
                    using var reader = again.ExecuteReader();
                    if (reader.Read())
                    {
                        var m = new Message(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                            reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
                        return new SendOutcome(m, "duplicate", null);
                    }
                }
                return new SendOutcome(null, "retry", "write collision, retry the send");
            }

            tx.Commit();
            var message = new Message(id, ConversationId(sender, recipient), seq, sender, recipient, text, now);
            return new SendOutcome(message, "created", null);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// One conversation id per pair, same whichever way it's asked
    /// (smaller id x 100 million + bigger id). Requires user ids &lt; 8 digits.
    /// TODO: switch to proper unique ids for users
    /// </summary>
    public static long ConversationId(int a, int b)
    {
        long lo = Math.Min(a, b), hi = Math.Max(a, b);
        return lo * 100_000_000L + hi;
    }

    private static object? Scalar(SqliteConnection con, string sql, params (string, object)[] args)
        => Scalar(con, sql, transaction: null, args);

    private static object? Scalar(SqliteConnection con, string sql, Microsoft.Data.Sqlite.SqliteTransaction? transaction, params (string, object)[] args)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return cmd.ExecuteScalar();
    }
}
