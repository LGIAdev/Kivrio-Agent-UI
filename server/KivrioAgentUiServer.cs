using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KivrioAgentUi
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string root = null;
            string host = "127.0.0.1";
            int port = 8000;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg == "--help" || arg == "-h")
                {
                    Console.WriteLine("usage: kivrio-agent-ui-server.exe [--root ROOT] [--host HOST] [--port PORT]");
                    return 0;
                }
                if (arg == "--root" && i + 1 < args.Length)
                {
                    root = args[++i];
                    continue;
                }
                if (arg == "--host" && i + 1 < args.Length)
                {
                    host = args[++i];
                    continue;
                }
                if (arg == "--port" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out port);
                    continue;
                }
            }

            if (port <= 0)
            {
                port = 8000;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                root = Path.GetFullPath(Path.Combine(exeDir, ".."));
            }

            root = Path.GetFullPath(root);
            var server = new LocalServer(root, host, port);
            Console.WriteLine("Kivrio Agent UI server running on http://" + host + ":" + port + "/index.html");
            Console.WriteLine("Application root: " + root);
            server.Run();
            return 0;
        }
    }

    internal sealed class LocalServer
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");
        private const string SessionCookieName = "kivro_session";
        private const int PasswordMinLength = 8;
        private const int PasswordMaxLength = 128;
        private const int Pbkdf2Iterations = 310000;
        private readonly string _root;
        private readonly string _host;
        private readonly int _port;
        private readonly JavaScriptSerializer _json;
        private readonly DataStore _store;
        private readonly string _authPath;
        private readonly bool _authEnabled;
        private readonly bool _sessionCookieSecure;
        private readonly int _sessionTtlSeconds;
        private readonly string _configuredAdminPassword;
        private readonly object _sessionsLock = new object();
        private readonly Dictionary<string, DateTime> _sessions = new Dictionary<string, DateTime>();

        public LocalServer(string root, string host, int port)
        {
            _root = root;
            _host = host;
            _port = port;
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _store = new DataStore(Path.Combine(root, "data"));
            _authPath = Path.Combine(root, "data", "auth.json");
            _authEnabled = !EnvFlag("KIVRO_DISABLE_AUTH", false);
            _sessionCookieSecure = EnvFlag("KIVRO_COOKIE_SECURE", false);
            _sessionTtlSeconds = Math.Max(300, ReadIntEnv("KIVRO_SESSION_TTL_SECONDS", 43200));
            _configuredAdminPassword = (Environment.GetEnvironmentVariable("KIVRO_ADMIN_PASSWORD") ?? "").Trim();
        }

        public void Run()
        {
            IPAddress address;
            if (!IPAddress.TryParse(_host, out address))
            {
                address = IPAddress.Loopback;
            }

            var listener = new TcpListener(address, _port);
            listener.Start();
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    NetworkStream stream = client.GetStream();
                    HttpRequest request = HttpRequest.Read(stream);
                    if (request == null)
                    {
                        return;
                    }

                    HttpResponse response = Route(request);
                    response.Write(stream);
                }
                catch (Exception ex)
                {
                    try
                    {
                        JsonError(HttpStatusCode.InternalServerError, ex.Message).Write(client.GetStream());
                    }
                    catch
                    {
                    }
                }
            }
        }

        private HttpResponse Route(HttpRequest request)
        {
            if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                return RouteApi(request);
            }

            return ServeStatic(request);
        }

        private HttpResponse RouteApi(HttpRequest request)
        {
            string method = request.Method;
            string path = request.Path;

            if (method == "GET" && path == "/api/health")
            {
                return Json(new Dictionary<string, object> { { "ok", true }, { "store", "json" } });
            }

            if (method == "GET" && path == "/api/auth/status")
            {
                return Json(AuthStatus(IsAuthenticated(request)));
            }

            if (method == "POST" && path == "/api/auth/setup")
            {
                if (!_authEnabled)
                {
                    return Json(AuthStatus(true));
                }
                if (!string.IsNullOrEmpty(_configuredAdminPassword))
                {
                    return JsonError(HttpStatusCode.Conflict, "La creation locale du mot de passe est desactivee quand KIVRO_ADMIN_PASSWORD est defini.");
                }
                if (ReadLocalAuthRecord() != null)
                {
                    return JsonError(HttpStatusCode.Conflict, "Le mot de passe est deja configure.");
                }

                Dictionary<string, object> body = ReadJsonObject(request);
                string password = ValidatePassword(GetBodyString(body, "password"));
                PersistLocalPassword(password);
                string token = CreateSession();
                HttpResponse response = Json(AuthStatus(true));
                response.Headers["Set-Cookie"] = BuildSessionCookie(token, false);
                return response;
            }

            if (method == "POST" && path == "/api/auth/login")
            {
                if (!_authEnabled)
                {
                    return Json(AuthStatus(true));
                }
                if (string.IsNullOrEmpty(_configuredAdminPassword) && ReadLocalAuthRecord() == null)
                {
                    return JsonError(HttpStatusCode.Conflict, "Password setup required.");
                }

                Dictionary<string, object> body = ReadJsonObject(request);
                if (!VerifyPassword(GetBodyString(body, "password")))
                {
                    return JsonError(HttpStatusCode.Unauthorized, "Invalid credentials.");
                }

                string token = CreateSession();
                HttpResponse response = Json(AuthStatus(true));
                response.Headers["Set-Cookie"] = BuildSessionCookie(token, false);
                return response;
            }

            if (method == "POST" && path == "/api/auth/logout")
            {
                RevokeSession(GetSessionToken(request));
                Dictionary<string, object> payload = AuthStatus(false);
                payload["ok"] = true;
                HttpResponse response = Json(payload);
                response.Headers["Set-Cookie"] = BuildSessionCookie("", true);
                return response;
            }

            if (!IsAuthenticated(request))
            {
                return JsonError(HttpStatusCode.Unauthorized, "Authentication required.");
            }

            if (method == "GET" && path == "/api/system-prompt")
            {
                return Json(_store.GetSystemPrompt());
            }
            if (method == "POST" && path == "/api/system-prompt")
            {
                return Json(_store.UpdateSystemPrompt(ReadJsonObject(request)));
            }

            if (method == "GET" && path == "/api/conversations")
            {
                return Json(_store.ListConversations());
            }
            if (method == "POST" && path == "/api/conversations")
            {
                return Json(_store.CreateConversation(ReadJsonObject(request)), HttpStatusCode.Created);
            }

            if (method == "GET" && path == "/api/folders")
            {
                return Json(_store.ListFolders());
            }
            if (method == "POST" && path == "/api/folders")
            {
                return Json(_store.CreateFolder(ReadJsonObject(request)), HttpStatusCode.Created);
            }

            if (path.StartsWith("/api/attachments/", StringComparison.OrdinalIgnoreCase))
            {
                return RouteAttachment(request);
            }

            string[] parts = SplitPath(path);
            if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "conversations")
            {
                string conversationId = Uri.UnescapeDataString(parts[2]);
                if (method == "GET" && parts.Length == 3)
                {
                    Dictionary<string, object> item = _store.GetConversationPayload(conversationId);
                    if (item == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(item);
                }
                if (method == "PATCH" && parts.Length == 3)
                {
                    Dictionary<string, object> item = _store.UpdateConversation(conversationId, ReadJsonObject(request));
                    if (item == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(item);
                }
                if (method == "DELETE" && parts.Length == 3)
                {
                    if (!_store.DeleteConversation(conversationId)) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(new Dictionary<string, object> { { "ok", true } });
                }
                if (method == "GET" && parts.Length == 4 && parts[3] == "messages")
                {
                    List<Dictionary<string, object>> messages = _store.GetConversationMessages(conversationId);
                    if (messages == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(messages);
                }
                if (method == "POST" && parts.Length == 4 && parts[3] == "messages")
                {
                    Dictionary<string, object> message = _store.AddMessage(conversationId, ReadJsonObject(request));
                    if (message == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(message, HttpStatusCode.Created);
                }
                if (method == "PATCH" && parts.Length == 5 && parts[3] == "messages")
                {
                    Dictionary<string, object> payload = _store.UpdateMessage(conversationId, Uri.UnescapeDataString(parts[4]), ReadJsonObject(request));
                    if (payload == null) return JsonError(HttpStatusCode.NotFound, "Message introuvable.");
                    return Json(payload);
                }
                if (method == "POST" && parts.Length == 4 && parts[3] == "attachments")
                {
                    List<UploadedFile> files = ReadMultipartFiles(request);
                    return Json(new Dictionary<string, object> { { "attachments", _store.CreateAttachments(conversationId, files) } }, HttpStatusCode.Created);
                }
            }

            if (parts.Length == 3 && parts[0] == "api" && parts[1] == "folders")
            {
                string folderId = Uri.UnescapeDataString(parts[2]);
                if (method == "PATCH")
                {
                    Dictionary<string, object> folder = _store.UpdateFolder(folderId, ReadJsonObject(request));
                    if (folder == null) return JsonError(HttpStatusCode.NotFound, "Dossier introuvable.");
                    return Json(folder);
                }
                if (method == "DELETE")
                {
                    if (!_store.DeleteFolder(folderId)) return JsonError(HttpStatusCode.NotFound, "Dossier introuvable.");
                    return Json(new Dictionary<string, object> { { "ok", true } });
                }
            }

            return JsonError(HttpStatusCode.NotFound, "Endpoint introuvable.");
        }

        private HttpResponse RouteAttachment(HttpRequest request)
        {
            string[] parts = SplitPath(request.Path);
            if (request.Method != "GET" || parts.Length != 4 || parts[0] != "api" || parts[1] != "attachments")
            {
                return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
            }

            AttachmentRecord attachment = _store.GetAttachment(Uri.UnescapeDataString(parts[2]));
            if (attachment == null)
            {
                return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
            }

            string filePath = _store.GetAttachmentPath(attachment);
            if (!File.Exists(filePath))
            {
                return JsonError(HttpStatusCode.NotFound, "Fichier joint introuvable.");
            }

            if (parts[3] == "view")
            {
                string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" +
                    HtmlEscape(attachment.filename) +
                    "</title><style>body{margin:0;background:#0f172a;display:grid;place-items:center;min-height:100vh}img{max-width:96vw;max-height:96vh;background:white}</style></head><body><img src=\"/api/attachments/" +
                    Uri.EscapeDataString(attachment.id) +
                    "/content\" alt=\"" +
                    HtmlEscape(attachment.filename) +
                    "\"></body></html>";
                return new HttpResponse(HttpStatusCode.OK, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            }

            if (parts[3] == "content")
            {
                return new HttpResponse(HttpStatusCode.OK, attachment.mimeType ?? "application/octet-stream", File.ReadAllBytes(filePath));
            }

            return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
        }

        private HttpResponse ServeStatic(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                return JsonError(HttpStatusCode.MethodNotAllowed, "Method not allowed.");
            }

            string path = request.Path;
            if (path == "/")
            {
                path = "/index.html";
            }

            if (!IsPublicStaticPath(path))
            {
                return JsonError(HttpStatusCode.NotFound, "Resource not found.");
            }

            string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(_root, relative));
            if (!fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                return JsonError(HttpStatusCode.NotFound, "Resource not found.");
            }

            byte[] body = request.Method == "HEAD" ? new byte[0] : File.ReadAllBytes(fullPath);
            return new HttpResponse(HttpStatusCode.OK, MimeTypeFor(fullPath), body);
        }

        private static bool IsPublicStaticPath(string path)
        {
            return path == "/index.html"
                || path == "/favicon.ico"
                || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, object> ReadJsonObject(HttpRequest request)
        {
            if (request.Body == null || request.Body.Length == 0)
            {
                return new Dictionary<string, object>();
            }

            object parsed = _json.DeserializeObject(Encoding.UTF8.GetString(request.Body));
            var dict = parsed as Dictionary<string, object>;
            return dict ?? new Dictionary<string, object>();
        }

        private List<UploadedFile> ReadMultipartFiles(HttpRequest request)
        {
            string contentType = request.GetHeader("Content-Type") ?? "";
            string boundary = ExtractBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                throw new InvalidOperationException("Boundary multipart introuvable.");
            }
            return MultipartParser.Parse(request.Body ?? new byte[0], boundary);
        }

        private HttpResponse Json(object payload)
        {
            return Json(payload, HttpStatusCode.OK);
        }

        private HttpResponse Json(object payload, HttpStatusCode status)
        {
            byte[] body = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            var response = new HttpResponse(status, "application/json; charset=utf-8", body);
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            return response;
        }

        private HttpResponse JsonError(HttpStatusCode status, string message)
        {
            return Json(new Dictionary<string, object> { { "error", message } }, status);
        }

        private Dictionary<string, object> AuthStatus(bool authenticated)
        {
            bool setupRequired = _authEnabled && string.IsNullOrEmpty(_configuredAdminPassword) && ReadLocalAuthRecord() == null;
            string passwordSource = !_authEnabled
                ? "disabled"
                : !string.IsNullOrEmpty(_configuredAdminPassword)
                    ? "environment"
                    : setupRequired
                        ? "unconfigured"
                        : "local";

            return new Dictionary<string, object>
            {
                { "enabled", _authEnabled },
                { "authenticated", !_authEnabled || authenticated },
                { "setupRequired", setupRequired },
                { "passwordSource", passwordSource },
                { "ok", true }
            };
        }

        private bool IsAuthenticated(HttpRequest request)
        {
            if (!_authEnabled)
            {
                return true;
            }

            string token = GetSessionToken(request);
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            PurgeExpiredSessions();
            lock (_sessionsLock)
            {
                DateTime expiresAt;
                if (!_sessions.TryGetValue(token, out expiresAt) || expiresAt <= DateTime.UtcNow)
                {
                    _sessions.Remove(token);
                    return false;
                }

                _sessions[token] = DateTime.UtcNow.AddSeconds(_sessionTtlSeconds);
                return true;
            }
        }

        private string GetSessionToken(HttpRequest request)
        {
            string raw = request.GetHeader("Cookie") ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int index = part.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                string name = part.Substring(0, index).Trim();
                if (string.Equals(name, SessionCookieName, StringComparison.Ordinal))
                {
                    return part.Substring(index + 1).Trim();
                }
            }

            return null;
        }

        private string CreateSession()
        {
            PurgeExpiredSessions();
            string token = GenerateToken(32);
            lock (_sessionsLock)
            {
                _sessions[token] = DateTime.UtcNow.AddSeconds(_sessionTtlSeconds);
            }
            return token;
        }

        private void RevokeSession(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return;
            }

            lock (_sessionsLock)
            {
                _sessions.Remove(token);
            }
        }

        private void PurgeExpiredSessions()
        {
            lock (_sessionsLock)
            {
                var expired = new List<string>();
                foreach (var pair in _sessions)
                {
                    if (pair.Value <= DateTime.UtcNow)
                    {
                        expired.Add(pair.Key);
                    }
                }
                foreach (string token in expired)
                {
                    _sessions.Remove(token);
                }
            }
        }

        private string BuildSessionCookie(string token, bool clear)
        {
            var parts = new List<string>
            {
                SessionCookieName + "=" + token,
                "Path=/",
                "HttpOnly",
                "SameSite=Lax",
                clear ? "Max-Age=0" : "Max-Age=" + _sessionTtlSeconds
            };
            if (clear)
            {
                parts.Add("Expires=Thu, 01 Jan 1970 00:00:00 GMT");
            }
            if (_sessionCookieSecure)
            {
                parts.Add("Secure");
            }
            return string.Join("; ", parts.ToArray());
        }

        private Dictionary<string, object> ReadLocalAuthRecord()
        {
            if (!File.Exists(_authPath))
            {
                return null;
            }

            try
            {
                object parsed = _json.DeserializeObject(File.ReadAllText(_authPath, Encoding.UTF8));
                var record = parsed as Dictionary<string, object>;
                if (record == null
                    || !record.ContainsKey("salt")
                    || !record.ContainsKey("passwordHash")
                    || !record.ContainsKey("iterations")
                    || !record.ContainsKey("createdAt"))
                {
                    return null;
                }
                return record;
            }
            catch
            {
                return null;
            }
        }

        private void PersistLocalPassword(string password)
        {
            byte[] salt = RandomBytes(16);
            byte[] digest = Pbkdf2Sha256(password, salt, Pbkdf2Iterations, 32);
            var record = new Dictionary<string, object>
            {
                { "salt", Convert.ToBase64String(salt) },
                { "passwordHash", Convert.ToBase64String(digest) },
                { "iterations", Pbkdf2Iterations },
                { "createdAt", UnixTimeSeconds() }
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_authPath));
            string tempPath = _authPath + ".tmp";
            File.WriteAllText(tempPath, _json.Serialize(record), Encoding.UTF8);
            if (File.Exists(_authPath))
            {
                File.Delete(_authPath);
            }
            File.Move(tempPath, _authPath);
        }

        private bool VerifyPassword(string password)
        {
            if (!_authEnabled)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(_configuredAdminPassword))
            {
                return ConstantTimeEquals(
                    Encoding.UTF8.GetBytes(password ?? ""),
                    Encoding.UTF8.GetBytes(_configuredAdminPassword));
            }

            Dictionary<string, object> record = ReadLocalAuthRecord();
            if (record == null)
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(Convert.ToString(record["salt"]) ?? "");
                byte[] expected = Convert.FromBase64String(Convert.ToString(record["passwordHash"]) ?? "");
                int iterations = Convert.ToInt32(record["iterations"]);
                if (salt.Length == 0 || expected.Length == 0 || iterations <= 0)
                {
                    return false;
                }

                byte[] computed = Pbkdf2Sha256(password ?? "", salt, iterations, expected.Length);
                return ConstantTimeEquals(computed, expected);
            }
            catch
            {
                return false;
            }
        }

        private static string ValidatePassword(string password)
        {
            string value = password ?? "";
            if (value.Length < PasswordMinLength)
            {
                throw new InvalidOperationException("Le mot de passe doit contenir au moins " + PasswordMinLength + " caracteres.");
            }
            if (value.Length > PasswordMaxLength)
            {
                throw new InvalidOperationException("Le mot de passe ne peut pas depasser " + PasswordMaxLength + " caracteres.");
            }
            return value;
        }

        private static string GetBodyString(Dictionary<string, object> body, string key)
        {
            if (body == null || !body.ContainsKey(key) || body[key] == null)
            {
                return "";
            }
            return Convert.ToString(body[key]) ?? "";
        }

        private static bool EnvFlag(string name, bool defaultValue)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (raw == null)
            {
                return defaultValue;
            }

            raw = raw.Trim().ToLowerInvariant();
            return raw == "1" || raw == "true" || raw == "yes" || raw == "on";
        }

        private static int ReadIntEnv(string name, int defaultValue)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            int value;
            return int.TryParse(raw, out value) ? value : defaultValue;
        }

        private static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        private static long UnixTimeSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string GenerateToken(int byteCount)
        {
            string token = Convert.ToBase64String(RandomBytes(byteCount));
            return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Pbkdf2Sha256(string password, byte[] salt, int iterations, int length)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password ?? "")))
            {
                int hashLength = hmac.HashSize / 8;
                int blockCount = (int)Math.Ceiling((double)length / hashLength);
                byte[] output = new byte[length];
                int offset = 0;

                for (int blockIndex = 1; blockIndex <= blockCount; blockIndex++)
                {
                    byte[] block = Pbkdf2Block(hmac, salt, iterations, blockIndex);
                    int bytesToCopy = Math.Min(hashLength, length - offset);
                    Buffer.BlockCopy(block, 0, output, offset, bytesToCopy);
                    offset += bytesToCopy;
                }

                return output;
            }
        }

        private static byte[] Pbkdf2Block(HMACSHA256 hmac, byte[] salt, int iterations, int blockIndex)
        {
            byte[] input = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
            input[input.Length - 4] = (byte)(blockIndex >> 24);
            input[input.Length - 3] = (byte)(blockIndex >> 16);
            input[input.Length - 2] = (byte)(blockIndex >> 8);
            input[input.Length - 1] = (byte)blockIndex;

            byte[] u = hmac.ComputeHash(input);
            byte[] result = (byte[])u.Clone();
            for (int i = 1; i < iterations; i++)
            {
                u = hmac.ComputeHash(u);
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] ^= u[j];
                }
            }
            return result;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string[] SplitPath(string path)
        {
            return (path ?? "").Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string ExtractBoundary(string contentType)
        {
            const string key = "boundary=";
            int index = contentType.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "";
            string value = contentType.Substring(index + key.Length).Trim();
            int semi = value.IndexOf(';');
            if (semi >= 0) value = value.Substring(0, semi).Trim();
            return value.Trim('"');
        }

        private static string MimeTypeFor(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".html") return "text/html; charset=utf-8";
            if (ext == ".css") return "text/css; charset=utf-8";
            if (ext == ".js" || ext == ".mjs") return "application/javascript; charset=utf-8";
            if (ext == ".json") return "application/json; charset=utf-8";
            if (ext == ".svg") return "image/svg+xml";
            if (ext == ".png") return "image/png";
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".webp") return "image/webp";
            if (ext == ".ico") return "image/x-icon";
            if (ext == ".woff2") return "font/woff2";
            if (ext == ".woff") return "font/woff";
            if (ext == ".ttf") return "font/ttf";
            return "application/octet-stream";
        }

        private static string HtmlEscape(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }

    internal sealed class HttpRequest
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

        public string Method;
        public string Target;
        public string Path;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string GetHeader(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public static HttpRequest Read(NetworkStream stream)
        {
            byte[] headerBytes = ReadHeaderBytes(stream);
            if (headerBytes == null || headerBytes.Length == 0)
            {
                return null;
            }

            string header = Latin1.GetString(headerBytes);
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var request = new HttpRequest();
            request.Method = requestLine[0].ToUpperInvariant();
            request.Target = requestLine[1];
            int queryIndex = request.Target.IndexOf('?');
            string rawPath = queryIndex >= 0 ? request.Target.Substring(0, queryIndex) : request.Target;
            request.Path = Uri.UnescapeDataString(rawPath);
            if (string.IsNullOrEmpty(request.Path)) request.Path = "/";

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            int contentLength = 0;
            string rawLength;
            if (request.Headers.TryGetValue("Content-Length", out rawLength))
            {
                int.TryParse(rawLength, out contentLength);
            }

            if (contentLength > 0)
            {
                request.Body = ReadExact(stream, contentLength);
            }

            return request;
        }

        private static byte[] ReadHeaderBytes(NetworkStream stream)
        {
            var buffer = new List<byte>();
            int matched = 0;
            byte[] marker = new byte[] { 13, 10, 13, 10 };
            while (buffer.Count < 65536)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                byte b = (byte)value;
                buffer.Add(b);
                if (b == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length) break;
                }
                else
                {
                    matched = b == marker[0] ? 1 : 0;
                }
            }
            return buffer.ToArray();
        }

        private static byte[] ReadExact(NetworkStream stream, int length)
        {
            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(body, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset == length) return body;
            byte[] partial = new byte[offset];
            Buffer.BlockCopy(body, 0, partial, 0, offset);
            return partial;
        }
    }

    internal sealed class HttpResponse
    {
        public readonly HttpStatusCode StatusCode;
        public readonly string ContentType;
        public readonly byte[] Body;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HttpResponse(HttpStatusCode statusCode, string contentType, byte[] body)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            Body = body ?? new byte[0];
            Headers["Connection"] = "close";
            Headers["X-Content-Type-Options"] = "nosniff";
            Headers["Referrer-Policy"] = "no-referrer";
        }

        public void Write(NetworkStream stream)
        {
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append((int)StatusCode).Append(' ').Append(ReasonPhrase(StatusCode)).Append("\r\n");
            builder.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            builder.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
            foreach (var pair in Headers)
            {
                builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
            builder.Append("\r\n");

            byte[] header = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(header, 0, header.Length);
            if (Body.Length > 0)
            {
                stream.Write(Body, 0, Body.Length);
            }
        }

        private static string ReasonPhrase(HttpStatusCode status)
        {
            if (status == HttpStatusCode.OK) return "OK";
            if (status == HttpStatusCode.Created) return "Created";
            if (status == HttpStatusCode.BadRequest) return "Bad Request";
            if (status == HttpStatusCode.Unauthorized) return "Unauthorized";
            if (status == HttpStatusCode.Forbidden) return "Forbidden";
            if (status == HttpStatusCode.NotFound) return "Not Found";
            if (status == HttpStatusCode.MethodNotAllowed) return "Method Not Allowed";
            if (status == HttpStatusCode.Gone) return "Gone";
            if (status == HttpStatusCode.InternalServerError) return "Internal Server Error";
            return status.ToString();
        }
    }

    internal sealed class DataStore
    {
        private readonly object _lock = new object();
        private readonly string _dataDir;
        private readonly string _storePath;
        private readonly string _uploadsDir;
        private readonly JavaScriptSerializer _json;
        private AppData _data;

        public DataStore(string dataDir)
        {
            _dataDir = dataDir;
            _storePath = Path.Combine(dataDir, "kivrio-agent-ui.json");
            _uploadsDir = Path.Combine(dataDir, "uploads");
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_uploadsDir);
            _data = Load();
        }

        public Dictionary<string, object> GetSystemPrompt()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>
                {
                    { "prompt", _data.systemPrompt ?? "" },
                    { "updatedAt", _data.systemPromptUpdatedAt }
                };
            }
        }

        public Dictionary<string, object> UpdateSystemPrompt(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                _data.systemPrompt = GetString(body, "prompt", "");
                _data.systemPromptUpdatedAt = NowMs();
                Save();
                return GetSystemPrompt();
            }
        }

        public List<Dictionary<string, object>> ListFolders()
        {
            lock (_lock)
            {
                var output = new List<Dictionary<string, object>>();
                foreach (FolderRecord folder in _data.folders)
                {
                    output.Add(SerializeFolder(folder));
                }
                output.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    return string.Compare(Convert.ToString(a["name"]), Convert.ToString(b["name"]), StringComparison.OrdinalIgnoreCase);
                });
                return output;
            }
        }

        public Dictionary<string, object> CreateFolder(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                long now = NowMs();
                var folder = new FolderRecord
                {
                    id = NewId("f"),
                    name = CleanTitle(GetString(body, "name", "Nouveau dossier"), "Nouveau dossier", 80),
                    createdAt = now,
                    updatedAt = now
                };
                _data.folders.Add(folder);
                Save();
                return SerializeFolder(folder);
            }
        }

        public Dictionary<string, object> UpdateFolder(string id, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                FolderRecord folder = FindFolder(id);
                if (folder == null) return null;
                if (body.ContainsKey("name"))
                {
                    folder.name = CleanTitle(GetString(body, "name", folder.name), folder.name, 80);
                }
                folder.updatedAt = NowMs();
                Save();
                return SerializeFolder(folder);
            }
        }

        public bool DeleteFolder(string id)
        {
            lock (_lock)
            {
                FolderRecord folder = FindFolder(id);
                if (folder == null) return false;
                _data.folders.Remove(folder);
                foreach (ConversationRecord conversation in _data.conversations)
                {
                    if (conversation.folderId == id) conversation.folderId = null;
                }
                Save();
                return true;
            }
        }

        public List<Dictionary<string, object>> ListConversations()
        {
            lock (_lock)
            {
                var output = new List<Dictionary<string, object>>();
                foreach (ConversationRecord conversation in _data.conversations)
                {
                    if (conversation.archived != 0) continue;
                    output.Add(SerializeConversation(conversation, false));
                }
                output.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    long left = Convert.ToInt64(a["updatedAt"]);
                    long right = Convert.ToInt64(b["updatedAt"]);
                    return right.CompareTo(left);
                });
                return output;
            }
        }

        public Dictionary<string, object> CreateConversation(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                long now = NowMs();
                var conversation = new ConversationRecord
                {
                    id = NewId("c"),
                    title = CleanTitle(GetString(body, "title", "Nouvelle conversation"), "Nouvelle conversation", 64),
                    folderId = GetString(body, "folder_id", GetString(body, "folderId", null)),
                    createdAt = now,
                    updatedAt = now,
                    archived = 0,
                    messages = new List<MessageRecord>()
                };
                _data.conversations.Add(conversation);
                Save();
                return SerializeConversation(conversation, true);
            }
        }

        public Dictionary<string, object> GetConversationPayload(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return null;
                return new Dictionary<string, object>
                {
                    { "conversation", SerializeConversation(conversation, false) },
                    { "messages", SerializeMessages(conversation) }
                };
            }
        }

        public List<Dictionary<string, object>> GetConversationMessages(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                return conversation == null ? null : SerializeMessages(conversation);
            }
        }

        public Dictionary<string, object> UpdateConversation(string id, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return null;
                if (body.ContainsKey("title")) conversation.title = CleanTitle(GetString(body, "title", conversation.title), conversation.title, 64);
                if (body.ContainsKey("folder_id")) conversation.folderId = GetNullableString(body, "folder_id");
                if (body.ContainsKey("folderId")) conversation.folderId = GetNullableString(body, "folderId");
                if (body.ContainsKey("archived")) conversation.archived = GetBool(body, "archived") ? 1 : 0;
                conversation.updatedAt = NowMs();
                Save();
                return SerializeConversation(conversation, false);
            }
        }

        public bool DeleteConversation(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return false;
                _data.conversations.Remove(conversation);
                Save();
                return true;
            }
        }

        public Dictionary<string, object> AddMessage(string conversationId, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null) return null;
                if (conversation.messages == null) conversation.messages = new List<MessageRecord>();

                long now = NowMs();
                var message = new MessageRecord
                {
                    id = NewId("m"),
                    conversationId = conversationId,
                    role = CleanRole(GetString(body, "role", "assistant")),
                    content = GetString(body, "content", ""),
                    reasoningText = GetNullableString(body, "reasoning_text") ?? GetNullableString(body, "reasoningText"),
                    model = GetNullableString(body, "model"),
                    reasoningDurationMs = GetNullableLong(body, "reasoning_duration_ms") ?? GetNullableLong(body, "reasoningDurationMs"),
                    createdAt = now,
                    position = conversation.messages.Count,
                    attachmentIds = GetStringList(body, "attachment_ids")
                };
                conversation.messages.Add(message);
                conversation.updatedAt = now;
                LinkAttachments(message);
                Save();
                return SerializeMessage(message);
            }
        }

        public Dictionary<string, object> UpdateMessage(string conversationId, string messageId, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null || conversation.messages == null) return null;
                int index = conversation.messages.FindIndex(delegate(MessageRecord item) { return item.id == messageId; });
                if (index < 0) return null;
                MessageRecord message = conversation.messages[index];
                if (body.ContainsKey("content")) message.content = GetString(body, "content", message.content);
                if (body.ContainsKey("role")) message.role = CleanRole(GetString(body, "role", message.role));
                if (body.ContainsKey("reasoning_text")) message.reasoningText = GetNullableString(body, "reasoning_text");
                if (body.ContainsKey("reasoningText")) message.reasoningText = GetNullableString(body, "reasoningText");
                if (body.ContainsKey("truncate_following") && GetBool(body, "truncate_following"))
                {
                    conversation.messages.RemoveRange(index + 1, conversation.messages.Count - index - 1);
                }
                conversation.updatedAt = NowMs();
                Save();
                return new Dictionary<string, object>
                {
                    { "conversation", SerializeConversation(conversation, false) },
                    { "messages", SerializeMessages(conversation) }
                };
            }
        }

        public List<Dictionary<string, object>> CreateAttachments(string conversationId, List<UploadedFile> files)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null) return new List<Dictionary<string, object>>();

                var result = new List<Dictionary<string, object>>();
                foreach (UploadedFile file in files)
                {
                    if (file == null || file.Content == null) continue;
                    string id = NewId("a");
                    string safeName = SafeFileName(file.FileName);
                    string relativeDir = Path.Combine("uploads", conversationId, id);
                    string absoluteDir = Path.Combine(_dataDir, relativeDir);
                    Directory.CreateDirectory(absoluteDir);
                    string absolutePath = Path.Combine(absoluteDir, safeName);
                    File.WriteAllBytes(absolutePath, file.Content);

                    var attachment = new AttachmentRecord
                    {
                        id = id,
                        conversationId = conversationId,
                        messageId = null,
                        filename = safeName,
                        mimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                        sizeBytes = file.Content.LongLength,
                        relativePath = Path.Combine(relativeDir, safeName),
                        createdAt = NowMs()
                    };
                    _data.attachments.Add(attachment);
                    result.Add(SerializeAttachment(attachment));
                }
                Save();
                return result;
            }
        }

        public AttachmentRecord GetAttachment(string id)
        {
            lock (_lock)
            {
                return _data.attachments.Find(delegate(AttachmentRecord item) { return item.id == id; });
            }
        }

        public string GetAttachmentPath(AttachmentRecord attachment)
        {
            string fullPath = Path.GetFullPath(Path.Combine(_dataDir, attachment.relativePath ?? ""));
            string dataRoot = Path.GetFullPath(_dataDir);
            if (!fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return fullPath;
        }

        private AppData Load()
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    AppData loaded = _json.Deserialize<AppData>(File.ReadAllText(_storePath, Encoding.UTF8));
                    return Normalize(loaded);
                }
            }
            catch
            {
            }
            return Normalize(new AppData());
        }

        private void Save()
        {
            Directory.CreateDirectory(_dataDir);
            string tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, _json.Serialize(_data), Encoding.UTF8);
            if (File.Exists(_storePath)) File.Delete(_storePath);
            File.Move(tempPath, _storePath);
        }

        private AppData Normalize(AppData data)
        {
            if (data == null) data = new AppData();
            if (data.folders == null) data.folders = new List<FolderRecord>();
            if (data.conversations == null) data.conversations = new List<ConversationRecord>();
            if (data.attachments == null) data.attachments = new List<AttachmentRecord>();
            foreach (ConversationRecord conversation in data.conversations)
            {
                if (conversation.messages == null) conversation.messages = new List<MessageRecord>();
                for (int i = 0; i < conversation.messages.Count; i++)
                {
                    MessageRecord message = conversation.messages[i];
                    if (message.attachmentIds == null) message.attachmentIds = new List<string>();
                    message.position = i;
                }
            }
            return data;
        }

        private ConversationRecord FindConversation(string id)
        {
            return _data.conversations.Find(delegate(ConversationRecord item) { return item.id == id; });
        }

        private FolderRecord FindFolder(string id)
        {
            return _data.folders.Find(delegate(FolderRecord item) { return item.id == id; });
        }

        private void LinkAttachments(MessageRecord message)
        {
            if (message.attachmentIds == null) return;
            foreach (string attachmentId in message.attachmentIds)
            {
                AttachmentRecord attachment = _data.attachments.Find(delegate(AttachmentRecord item) { return item.id == attachmentId; });
                if (attachment != null)
                {
                    attachment.messageId = message.id;
                }
            }
        }

        private Dictionary<string, object> SerializeFolder(FolderRecord folder)
        {
            return new Dictionary<string, object>
            {
                { "id", folder.id },
                { "name", folder.name },
                { "createdAt", folder.createdAt },
                { "updatedAt", folder.updatedAt },
                { "conversationCount", CountConversationsInFolder(folder.id) }
            };
        }

        private Dictionary<string, object> SerializeConversation(ConversationRecord conversation, bool includeMessages)
        {
            var result = new Dictionary<string, object>
            {
                { "id", conversation.id },
                { "title", conversation.title },
                { "folderId", conversation.folderId },
                { "createdAt", conversation.createdAt },
                { "updatedAt", conversation.updatedAt },
                { "archived", conversation.archived },
                { "messageCount", conversation.messages == null ? 0 : conversation.messages.Count }
            };
            if (includeMessages)
            {
                result["messages"] = SerializeMessages(conversation);
            }
            return result;
        }

        private List<Dictionary<string, object>> SerializeMessages(ConversationRecord conversation)
        {
            var output = new List<Dictionary<string, object>>();
            if (conversation.messages == null) return output;
            foreach (MessageRecord message in conversation.messages)
            {
                output.Add(SerializeMessage(message));
            }
            return output;
        }

        private Dictionary<string, object> SerializeMessage(MessageRecord message)
        {
            return new Dictionary<string, object>
            {
                { "id", message.id },
                { "conversationId", message.conversationId },
                { "role", message.role },
                { "content", message.content ?? "" },
                { "reasoningText", message.reasoningText },
                { "model", message.model },
                { "reasoningDurationMs", message.reasoningDurationMs },
                { "createdAt", message.createdAt },
                { "position", message.position },
                { "attachments", SerializeAttachmentsForMessage(message.id) }
            };
        }

        private List<Dictionary<string, object>> SerializeAttachmentsForMessage(string messageId)
        {
            var output = new List<Dictionary<string, object>>();
            foreach (AttachmentRecord attachment in _data.attachments)
            {
                if (attachment.messageId == messageId)
                {
                    output.Add(SerializeAttachment(attachment));
                }
            }
            return output;
        }

        private Dictionary<string, object> SerializeAttachment(AttachmentRecord attachment)
        {
            bool isImage = (attachment.mimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            string contentUrl = "/api/attachments/" + Uri.EscapeDataString(attachment.id) + "/content";
            return new Dictionary<string, object>
            {
                { "id", attachment.id },
                { "conversationId", attachment.conversationId },
                { "messageId", attachment.messageId },
                { "filename", attachment.filename },
                { "mimeType", attachment.mimeType },
                { "sizeBytes", attachment.sizeBytes },
                { "url", contentUrl },
                { "previewUrl", isImage ? contentUrl : null },
                { "isImage", isImage },
                { "status", "stored" }
            };
        }

        private int CountConversationsInFolder(string folderId)
        {
            int count = 0;
            foreach (ConversationRecord conversation in _data.conversations)
            {
                if (conversation.folderId == folderId && conversation.archived == 0) count++;
            }
            return count;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static string NewId(string prefix)
        {
            return prefix + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static string CleanTitle(string value, string fallback, int max)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) text = fallback;
            if (text.Length > max) text = text.Substring(0, max);
            return text;
        }

        private static string CleanRole(string value)
        {
            string role = (value ?? "").Trim().ToLowerInvariant();
            if (role == "user" || role == "assistant" || role == "system") return role;
            return "assistant";
        }

        private static string SafeFileName(string value)
        {
            string name = Path.GetFileName(value ?? "fichier");
            if (string.IsNullOrWhiteSpace(name)) name = "fichier";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string GetString(Dictionary<string, object> body, string key, string fallback)
        {
            object value;
            if (body != null && body.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return fallback;
        }

        private static string GetNullableString(Dictionary<string, object> body, string key)
        {
            object value;
            if (body != null && body.TryGetValue(key, out value) && value != null)
            {
                string text = Convert.ToString(value);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> body, string key)
        {
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            string text = Convert.ToString(value).Trim().ToLowerInvariant();
            return text == "1" || text == "true" || text == "yes" || text == "on";
        }

        private static long? GetNullableLong(Dictionary<string, object> body, string key)
        {
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return null;
            long parsed;
            if (long.TryParse(Convert.ToString(value), out parsed)) return parsed;
            return null;
        }

        private static List<string> GetStringList(Dictionary<string, object> body, string key)
        {
            var output = new List<string>();
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return output;
            object[] array = value as object[];
            if (array != null)
            {
                foreach (object item in array)
                {
                    if (item != null) output.Add(Convert.ToString(item));
                }
            }
            return output;
        }
    }

    public sealed class AppData
    {
        public string systemPrompt { get; set; }
        public long systemPromptUpdatedAt { get; set; }
        public List<FolderRecord> folders { get; set; }
        public List<ConversationRecord> conversations { get; set; }
        public List<AttachmentRecord> attachments { get; set; }
    }

    public sealed class FolderRecord
    {
        public string id { get; set; }
        public string name { get; set; }
        public long createdAt { get; set; }
        public long updatedAt { get; set; }
    }

    public sealed class ConversationRecord
    {
        public string id { get; set; }
        public string title { get; set; }
        public string folderId { get; set; }
        public long createdAt { get; set; }
        public long updatedAt { get; set; }
        public int archived { get; set; }
        public List<MessageRecord> messages { get; set; }
    }

    public sealed class MessageRecord
    {
        public string id { get; set; }
        public string conversationId { get; set; }
        public string role { get; set; }
        public string content { get; set; }
        public string reasoningText { get; set; }
        public string model { get; set; }
        public long? reasoningDurationMs { get; set; }
        public long createdAt { get; set; }
        public int position { get; set; }
        public List<string> attachmentIds { get; set; }
    }

    public sealed class AttachmentRecord
    {
        public string id { get; set; }
        public string conversationId { get; set; }
        public string messageId { get; set; }
        public string filename { get; set; }
        public string mimeType { get; set; }
        public long sizeBytes { get; set; }
        public string relativePath { get; set; }
        public long createdAt { get; set; }
    }

    internal sealed class UploadedFile
    {
        public string FileName;
        public string ContentType;
        public byte[] Content;
    }

    internal static class MultipartParser
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

        public static List<UploadedFile> Parse(byte[] body, string boundary)
        {
            var files = new List<UploadedFile>();
            string text = Latin1.GetString(body ?? new byte[0]);
            string marker = "--" + boundary;
            int position = 0;

            while (true)
            {
                int start = text.IndexOf(marker, position, StringComparison.Ordinal);
                if (start < 0) break;
                start += marker.Length;
                if (start + 1 < text.Length && text.Substring(start, 2) == "--") break;
                if (start + 1 < text.Length && text.Substring(start, 2) == "\r\n") start += 2;

                int headerEnd = text.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
                if (headerEnd < 0) break;
                int contentStart = headerEnd + 4;
                int next = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
                if (next < 0) break;
                int contentEnd = next;
                if (contentEnd >= 2 && text.Substring(contentEnd - 2, 2) == "\r\n") contentEnd -= 2;

                string headerText = text.Substring(start, headerEnd - start);
                string contentText = text.Substring(contentStart, Math.Max(0, contentEnd - contentStart));
                var headers = ParseHeaders(headerText);
                string disposition;
                if (headers.TryGetValue("Content-Disposition", out disposition))
                {
                    string fileName = HeaderParameter(disposition, "filename");
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        string contentType;
                        headers.TryGetValue("Content-Type", out contentType);
                        files.Add(new UploadedFile
                        {
                            FileName = fileName,
                            ContentType = contentType ?? "application/octet-stream",
                            Content = Latin1.GetBytes(contentText)
                        });
                    }
                }
                position = next;
            }

            return files;
        }

        private static Dictionary<string, string> ParseHeaders(string text)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            return headers;
        }

        private static string HeaderParameter(string header, string name)
        {
            string[] parts = (header ?? "").Split(';');
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                string key = part.Substring(0, equals).Trim();
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                return part.Substring(equals + 1).Trim().Trim('"');
            }
            return "";
        }
    }
}
