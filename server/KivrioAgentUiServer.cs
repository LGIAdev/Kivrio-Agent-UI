using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly CodexAgentBridge _agentBridge;
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
            _agentBridge = new CodexAgentBridge(root);
            _agentBridge.EnsureDefaultOpenCodeWorkspace();
            _agentBridge.StartInBackground();
            AppDomain.CurrentDomain.ProcessExit += delegate { _agentBridge.Stop(); };
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
            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
                try { _agentBridge.Stop(); } catch { }
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
                StopAgentAfterLogout();
                return response;
            }

            if (!IsAuthenticated(request))
            {
                return JsonError(HttpStatusCode.Unauthorized, "Authentication required.");
            }

            if (method == "GET" && path == "/api/agent/status")
            {
                return Json(_agentBridge.Status(true, GetQueryString(request, "agent")));
            }

            if (method == "GET" && path == "/api/agent/diagnostic")
            {
                return Json(_agentBridge.Diagnostic(GetQueryString(request, "agent")));
            }

            if (method == "POST" && path == "/api/agent/opencode/workspace")
            {
                Dictionary<string, object> body = ReadJsonObject(request);
                try
                {
                    return Json(_agentBridge.ResolveOpenCodeWorkspace(GetBodyObject(body, "workspace"), GetBodyBool(body, "create")));
                }
                catch (Exception ex)
                {
                    return JsonError(HttpStatusCode.BadRequest, ex.Message);
                }
            }

            if (method == "POST" && path == "/api/agent/chat")
            {
                Dictionary<string, object> body = ReadJsonObject(request);
                string prompt = GetBodyString(body, "prompt").Trim();
                if (string.IsNullOrEmpty(prompt))
                {
                    return JsonError(HttpStatusCode.BadRequest, "Message vide.");
                }

                string systemPrompt = GetBodyString(body, "systemPrompt");
                string model = GetBodyString(body, "model");
                string profile = GetBodyString(body, "profile");
                string agent = GetBodyString(body, "agent");
                try
                {
                    return Json(_agentBridge.Chat(prompt, systemPrompt, model, profile, agent, GetBodyObject(body, "openCodeWorkspace")));
                }
                catch (Exception ex)
                {
                    return JsonError(HttpStatusCode.BadGateway, "Dialogue agent impossible: " + ex.Message);
                }
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

        private void StopAgentAfterLogout()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { _agentBridge.Stop(); } catch { }
            });
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
            var response = new HttpResponse(HttpStatusCode.OK, MimeTypeFor(fullPath), body);
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            response.Headers["Pragma"] = "no-cache";
            response.Headers["Expires"] = "0";
            return response;
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

        private static object GetBodyObject(Dictionary<string, object> body, string key)
        {
            if (body == null || !body.ContainsKey(key))
            {
                return null;
            }
            return body[key];
        }

        private static bool GetBodyBool(Dictionary<string, object> body, string key)
        {
            object value = GetBodyObject(body, key);
            if (value is bool)
            {
                return (bool)value;
            }
            string text = Convert.ToString(value ?? "").Trim();
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetQueryString(HttpRequest request, string key)
        {
            string target = request == null ? "" : (request.Target ?? "");
            int queryIndex = target.IndexOf('?');
            if (queryIndex < 0 || queryIndex + 1 >= target.Length) return "";

            string query = target.Substring(queryIndex + 1);
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i] ?? "";
                int equals = pair.IndexOf('=');
                string rawName = equals >= 0 ? pair.Substring(0, equals) : pair;
                if (!string.Equals(Uri.UnescapeDataString(rawName.Replace("+", " ")), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string rawValue = equals >= 0 ? pair.Substring(equals + 1) : "";
                return Uri.UnescapeDataString(rawValue.Replace("+", " "));
            }
            return "";
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

    internal static class LocalAgentConfig
    {
        public const string Agent = "codex";
        public const string AgentLabel = "Codex CLI";
        public const string Provider = "ollama";
        public const string ProviderLabel = "Ollama";
        public const string Mode = "ollama-launch";
        public const string DefaultModel = "gpt-oss:20b";
        public const string Channel = "Ollama launch Codex CLI/app-server";
        public const string RefuseNonOssModeMessage = "Mode refuse : Codex CLI doit etre lance via ollama launch codex pour utiliser Ollama localement.";
        public const string MissingModelMessage = "Aucun modele Ollama selectionne. Veuillez selectionner un modele local avant de lancer Codex CLI.";

        public static string ReadProvider()
        {
            string provider = (Environment.GetEnvironmentVariable("KIVRIO_CODEX_LOCAL_PROVIDER") ?? Provider).Trim();
            if (string.IsNullOrEmpty(provider)) provider = Provider;
            if (!string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Seul le provider local Ollama est autorise.");
            }
            return Provider;
        }

        public static string ReadDefaultModel()
        {
            string model = (Environment.GetEnvironmentVariable("KIVRIO_CODEX_MODEL") ?? DefaultModel).Trim();
            if (string.IsNullOrEmpty(model)) model = DefaultModel;
            return NormalizeModel(model);
        }

        public static string NormalizeModel(string model)
        {
            string value = (model ?? "").Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(MissingModelMessage);
            }
            return value;
        }
    }

    internal sealed class CodingAgentDefinition
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string Integration;
        public readonly string Channel;
        public readonly bool SupportsAppServer;
        public readonly string WslCommand;
        public readonly string WslFallbackPath;

        public CodingAgentDefinition(string id, string label, string integration, string channel, bool supportsAppServer, string wslCommand, string wslFallbackPath)
        {
            Id = id;
            Label = label;
            Integration = integration;
            Channel = channel;
            SupportsAppServer = supportsAppServer;
            WslCommand = wslCommand;
            WslFallbackPath = wslFallbackPath;
        }
    }

    internal static class CodingAgentCatalog
    {
        public const string DefaultId = "codex";

        private static readonly CodingAgentDefinition[] Agents = new[]
        {
            new CodingAgentDefinition("codex", "Codex CLI", "codex", "Ollama launch Codex CLI/app-server", true, "", ""),
            new CodingAgentDefinition("opencode", "OpenCode", "opencode", "WSL OpenCode", false, "opencode", "$HOME/.opencode/bin/opencode")
        };

        public static CodingAgentDefinition FromValue(string value)
        {
            string id = NormalizeId(value);
            for (int i = 0; i < Agents.Length; i++)
            {
                if (string.Equals(Agents[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return Agents[i];
                }
            }
            return Agents[0];
        }

        public static string NormalizeId(string value)
        {
            string id = (value ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) return DefaultId;
            if (id == "codex-cli" || id == "codex cli") return DefaultId;
            for (int i = 0; i < Agents.Length; i++)
            {
                if (string.Equals(Agents[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return Agents[i].Id;
                }
            }
            return DefaultId;
        }
    }

    internal sealed class AgentRunProfile
    {
        public const string FastId = "fast";
        public const string DeepId = "deep";

        public readonly string Id;
        public readonly string Label;
        public readonly int DefaultTimeoutMs;
        public readonly string DeveloperInstructions;
        public readonly string TurnInstructions;

        private AgentRunProfile(string id, string label, int defaultTimeoutMs, string developerInstructions, string turnInstructions)
        {
            Id = id;
            Label = label;
            DefaultTimeoutMs = defaultTimeoutMs;
            DeveloperInstructions = developerInstructions;
            TurnInstructions = turnInstructions;
        }

        public static AgentRunProfile FromValue(string value)
        {
            string id = (value ?? "").Trim().ToLowerInvariant();
            if (id == FastId || id == "rapide")
            {
                return Fast();
            }
            return Deep();
        }

        private static AgentRunProfile Fast()
        {
            return new AgentRunProfile(
                FastId,
                "Rapide",
                600000,
                "Profil Rapide actif: privilegie une reponse directe, concise et actionnable. Evite les explorations larges du depot. Lis seulement les fichiers indispensables. Si une action est demandee, fais le plus petit changement utile. Si une incertitude bloque, demande une clarification rapidement. ",
                "Profil Rapide: reponds court, evite les longues analyses visibles, propose au maximum 3 etapes si un plan est necessaire."
            );
        }

        private static AgentRunProfile Deep()
        {
            return new AgentRunProfile(
                DeepId,
                "Profond",
                1200000,
                "Profil Profond actif: privilegie un diagnostic complet et robuste. Explore les fichiers necessaires avant de modifier. Signale les risques et verifie davantage quand le changement touche plusieurs modules. ",
                "Profil Profond: prends le temps d'analyser les fichiers utiles et de verifier les impacts avant de conclure."
            );
        }
    }

    internal sealed class CodexAgentBridge
    {
        private const int DefaultPort = 17655;
        private const int MaxPortScan = 40;
        private readonly object _lock = new object();
        private readonly object _chatLock = new object();
        private readonly string _root;
        private Process _process;
        private string _launcherPath;
        private string _codexPath;
        private int _port;
        private bool _attachedToExisting;
        private string _lastError;
        private string _processModel;
        private DateTime? _startedAtUtc;

        public CodexAgentBridge(string root)
        {
            _root = root;
        }

        public void Start()
        {
            Status(true);
        }

        public void StartInBackground()
        {
            ThreadPool.QueueUserWorkItem(delegate { Start(); });
        }

        public void Stop()
        {
            lock (_lock)
            {
                StopProcessLocked();
            }
        }

        public Dictionary<string, object> Status(bool ensureStarted, string agent)
        {
            CodingAgentDefinition selectedAgent = CodingAgentCatalog.FromValue(agent);
            if (!selectedAgent.SupportsAppServer)
            {
                return UnsupportedAgentStatus(selectedAgent);
            }
            return Status(ensureStarted);
        }

        public Dictionary<string, object> Status(bool ensureStarted)
        {
            lock (_lock)
            {
                if (ensureStarted)
                {
                    EnsureStartedLocked();
                }

                bool ready = _port > 0 && ProbeHttp("http://127.0.0.1:" + _port + "/readyz", 500);
                bool healthy = _port > 0 && ProbeHttp("http://127.0.0.1:" + _port + "/healthz", 500);
                bool processRunning = _process != null && !_process.HasExited;

                string mode = "missing";
                if (ready || healthy)
                {
                    mode = _attachedToExisting ? "attached" : "owned";
                }
                else if (processRunning)
                {
                    mode = "starting";
                }
                else if (!string.IsNullOrEmpty(_lastError))
                {
                    mode = "error";
                }

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "agent", LocalAgentConfig.Agent },
                    { "agentLabel", LocalAgentConfig.AgentLabel },
                    { "integration", "codex" },
                    { "provider", LocalAgentConfig.Provider },
                    { "providerLabel", LocalAgentConfig.ProviderLabel },
                    { "agentMode", LocalAgentConfig.Mode },
                    { "defaultModel", LocalAgentConfig.DefaultModel },
                    { "processModel", _processModel ?? "" },
                    { "codexFound", !string.IsNullOrEmpty(_launcherPath) && File.Exists(_launcherPath) && !string.IsNullOrEmpty(_codexPath) && File.Exists(_codexPath) },
                    { "ollamaFound", !string.IsNullOrEmpty(_launcherPath) && File.Exists(_launcherPath) },
                    { "ollamaPath", _launcherPath ?? "" },
                    { "codexPath", _codexPath ?? "" },
                    { "mode", mode },
                    { "running", ready || healthy || processRunning },
                    { "ownedProcess", processRunning && !_attachedToExisting },
                    { "attachedToExisting", _attachedToExisting },
                    { "processId", processRunning ? _process.Id : 0 },
                    { "port", _port },
                    { "url", _port > 0 ? "ws://127.0.0.1:" + _port : "" },
                    { "ready", ready },
                    { "healthy", healthy },
                    { "startedAt", _startedAtUtc.HasValue ? UnixTimeSeconds(_startedAtUtc.Value) : 0 },
                    { "lastError", _lastError ?? "" }
                };
            }
        }

        public Dictionary<string, object> Diagnostic(string agent)
        {
            CodingAgentDefinition selectedAgent = CodingAgentCatalog.FromValue(agent);
            if (!selectedAgent.SupportsAppServer)
            {
                Dictionary<string, object> unsupported = UnsupportedAgentStatus(selectedAgent);
                unsupported["diagnostic"] = true;
                unsupported["channel"] = selectedAgent.Channel;
                unsupported["effectiveModel"] = LocalAgentConfig.DefaultModel;
                unsupported["effectiveProvider"] = LocalAgentConfig.Provider;
                unsupported["codexReady"] = false;
                unsupported["codexHealthy"] = false;
                unsupported["source"] = "Kivrio Agent UI server";
                return unsupported;
            }
            return Diagnostic();
        }

        public Dictionary<string, object> Diagnostic()
        {
            Dictionary<string, object> status = Status(true);
            bool ready = status.ContainsKey("ready") && status["ready"] is bool && (bool)status["ready"];
            bool healthy = status.ContainsKey("healthy") && status["healthy"] is bool && (bool)status["healthy"];

            status["diagnostic"] = true;
            status["channel"] = LocalAgentConfig.Channel;
            status["effectiveModel"] = LocalAgentConfig.DefaultModel;
            status["effectiveProvider"] = LocalAgentConfig.Provider;
            status["agent"] = LocalAgentConfig.Agent;
            status["agentLabel"] = LocalAgentConfig.AgentLabel;
            status["integration"] = "codex";
            status["providerLabel"] = LocalAgentConfig.ProviderLabel;
            status["agentMode"] = LocalAgentConfig.Mode;
            status["defaultModel"] = LocalAgentConfig.DefaultModel;
            status["processModel"] = _processModel ?? "";
            status["codexReady"] = ready;
            status["codexHealthy"] = healthy;
            status["source"] = "Kivrio Agent UI server";
            return status;
        }

        public Dictionary<string, object> Chat(string prompt, string systemPrompt, string model, string profile, string agent, object openCodeWorkspace)
        {
            CodingAgentDefinition selectedAgent = CodingAgentCatalog.FromValue(agent);
            if (!selectedAgent.SupportsAppServer)
            {
                return ChatWithWslAgent(selectedAgent, prompt, systemPrompt, model, profile, openCodeWorkspace);
            }

            string requestedModel = string.IsNullOrEmpty((model ?? "").Trim())
                ? LocalAgentConfig.ReadDefaultModel()
                : LocalAgentConfig.NormalizeModel(model);
            AgentRunProfile runProfile = AgentRunProfile.FromValue(profile);
            int port;
            lock (_lock)
            {
                EnsureStartedLocked(requestedModel);
                if (_port <= 0)
                {
                    throw new InvalidOperationException(_lastError ?? "codex app-server indisponible.");
                }
                port = _port;
            }

            lock (_chatLock)
            {
                var client = new CodexAppServerClient(port, _root);
                return client.RunTurn(prompt, systemPrompt, requestedModel, runProfile);
            }
        }

        private static Dictionary<string, object> UnsupportedAgentStatus(CodingAgentDefinition agent)
        {
            string launcherPath = FindOllamaCommand();
            bool ollamaFound = !string.IsNullOrEmpty(launcherPath) && File.Exists(launcherPath);
            WslAgentDetection wsl = DetectWslAgent(agent);
            bool dialogueConnected = wsl.AgentFound;
            string mode = dialogueConnected ? "wsl-ready" : (wsl.WslFound ? "wsl-agent-missing" : "wsl-unavailable");
            string message = dialogueConnected
                ? agent.Label + " detecte via WSL. Adaptateur de dialogue Kivrio Agent UI disponible."
                : agent.Label + " introuvable via WSL.";
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "agent", agent.Id },
                { "agentLabel", agent.Label },
                { "integration", agent.Integration },
                { "provider", LocalAgentConfig.Provider },
                { "providerLabel", LocalAgentConfig.ProviderLabel },
                { "agentMode", "wsl-optional" },
                { "defaultModel", LocalAgentConfig.DefaultModel },
                { "processModel", "" },
                { "codexFound", false },
                { "agentFound", wsl.AgentFound },
                { "ollamaFound", ollamaFound },
                { "ollamaPath", launcherPath ?? "" },
                { "codexPath", "" },
                { "agentPath", wsl.CommandPath ?? "" },
                { "wslFound", wsl.WslFound },
                { "wslPath", wsl.WslPath ?? "" },
                { "wslDistribution", wsl.Distribution ?? "" },
                { "wslCommandPath", wsl.CommandPath ?? "" },
                { "wslError", wsl.Error ?? "" },
                { "dialogueConnected", dialogueConnected },
                { "mode", mode },
                { "running", dialogueConnected },
                { "ownedProcess", false },
                { "attachedToExisting", false },
                { "processId", 0 },
                { "port", 0 },
                { "url", "" },
                { "ready", dialogueConnected },
                { "healthy", dialogueConnected },
                { "startedAt", 0 },
                { "lastError", message },
                { "channel", agent.Channel }
            };
        }

        public void EnsureDefaultOpenCodeWorkspace()
        {
            try
            {
                OpenCodeWorkspaceResolver.Resolve(null, _root, true);
            }
            catch
            {
            }
        }

        public Dictionary<string, object> ResolveOpenCodeWorkspace(object workspaceSettings, bool create)
        {
            return OpenCodeWorkspaceResolver.Resolve(workspaceSettings, _root, create).ToDictionary();
        }

        private Dictionary<string, object> ChatWithWslAgent(CodingAgentDefinition agent, string prompt, string systemPrompt, string model, string profile, object openCodeWorkspace)
        {
            string requestedModel = string.IsNullOrEmpty((model ?? "").Trim())
                ? LocalAgentConfig.ReadDefaultModel()
                : LocalAgentConfig.NormalizeModel(model);
            AgentRunProfile runProfile = AgentRunProfile.FromValue(profile);
            WslAgentDetection wsl = DetectWslAgent(agent);
            if (!wsl.WslFound)
            {
                throw new InvalidOperationException("WSL indisponible pour " + agent.Label + ": " + (wsl.Error ?? "wsl.exe introuvable."));
            }
            if (!wsl.AgentFound || string.IsNullOrWhiteSpace(wsl.CommandPath))
            {
                throw new InvalidOperationException(agent.Label + " introuvable dans WSL: " + (wsl.Error ?? "commande absente."));
            }

            string codexTestRoot = WslCliAgentClient.GetCodexTestRoot();
            string workspaceRoot = agent.Id == "opencode"
                ? OpenCodeWorkspaceResolver.Resolve(openCodeWorkspace, _root, true).WindowsPath
                : WslCliAgentClient.ResolveWorkspaceRoot(prompt, _root, codexTestRoot);
            var client = new WslCliAgentClient(agent, wsl, workspaceRoot, _root, codexTestRoot);
            return client.RunTurn(prompt, systemPrompt, requestedModel, runProfile);
        }

        private sealed class OpenCodeWorkspaceResult
        {
            public string BaseFolder;
            public string WorkDirectory;
            public string CustomBasePath;
            public string WindowsPath;
            public string WslPath;
            public bool Exists;
            public bool Created;
            public bool CreateRequested;
            public string Message;

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "baseFolder", BaseFolder ?? "" },
                    { "workDirectory", WorkDirectory ?? "" },
                    { "customBasePath", CustomBasePath ?? "" },
                    { "windowsPath", WindowsPath ?? "" },
                    { "wslPath", WslPath ?? "" },
                    { "exists", Exists },
                    { "created", Created },
                    { "createRequested", CreateRequested },
                    { "message", Message ?? "" }
                };
            }
        }

        private static class OpenCodeWorkspaceResolver
        {
            private const string DefaultBaseFolder = "documents";
            private const string DefaultWorkDirectory = "OpenCode";

            public static OpenCodeWorkspaceResult Resolve(object rawSettings, string kivrioRoot, bool create)
            {
                Dictionary<string, object> settings = rawSettings as Dictionary<string, object> ?? new Dictionary<string, object>();
                string baseFolder = NormalizeBaseFolder(ReadString(settings, "baseFolder"));
                string workDirectory = ReadString(settings, "workDirectory").Trim();
                string customBasePath = ReadString(settings, "customBasePath").Trim();
                if (string.IsNullOrWhiteSpace(workDirectory))
                {
                    workDirectory = DefaultWorkDirectory;
                }

                string basePath = ResolveBasePath(baseFolder, customBasePath);
                string target = ResolveTargetPath(basePath, workDirectory);

                if (PathsOverlap(target, kivrioRoot))
                {
                    throw new InvalidOperationException("Le dossier de travail OpenCode ne peut pas etre le dossier Kivrio Agent UI ni l'un de ses parents.");
                }

                bool existed = Directory.Exists(target);
                bool created = false;
                if (!existed && create)
                {
                    Directory.CreateDirectory(target);
                    created = true;
                }
                bool existsNow = Directory.Exists(target);
                string message = existsNow
                    ? (created ? "Dossier OpenCode cree et pret." : "Dossier OpenCode valide.")
                    : "Dossier introuvable. Il sera cree a l'enregistrement.";

                return new OpenCodeWorkspaceResult
                {
                    BaseFolder = baseFolder,
                    WorkDirectory = workDirectory,
                    CustomBasePath = customBasePath,
                    WindowsPath = target,
                    WslPath = WindowsPathToWsl(target),
                    Exists = existsNow,
                    Created = created,
                    CreateRequested = create,
                    Message = message
                };
            }

            private static string ReadString(Dictionary<string, object> settings, string key)
            {
                if (settings == null || !settings.ContainsKey(key) || settings[key] == null)
                {
                    return "";
                }
                return Convert.ToString(settings[key]) ?? "";
            }

            private static string NormalizeBaseFolder(string value)
            {
                string text = (value ?? "").Trim().ToLowerInvariant();
                if (text == "desktop" || text == "pictures" || text == "downloads" || text == "custom")
                {
                    return text;
                }
                return DefaultBaseFolder;
            }

            private static string ResolveBasePath(string baseFolder, string customBasePath)
            {
                string path = "";
                if (baseFolder == "custom")
                {
                    path = customBasePath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        throw new InvalidOperationException("Le chemin personnalise OpenCode est vide.");
                    }
                    if (!Path.IsPathRooted(path))
                    {
                        throw new InvalidOperationException("Le chemin personnalise OpenCode doit etre un chemin Windows complet.");
                    }
                    return Path.GetFullPath(path);
                }

                if (baseFolder == "desktop")
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                }
                else if (baseFolder == "pictures")
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }
                else if (baseFolder == "downloads")
                {
                    path = Path.Combine(GetUserProfile(), "Downloads");
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(GetUserProfile(), baseFolder == "desktop" ? "Desktop" : baseFolder == "pictures" ? "Pictures" : baseFolder == "downloads" ? "Downloads" : "Documents");
                }
                return Path.GetFullPath(path);
            }

            private static string ResolveTargetPath(string basePath, string workDirectory)
            {
                if (Path.IsPathRooted(workDirectory))
                {
                    throw new InvalidOperationException("Le repertoire de travail OpenCode doit rester relatif au dossier Windows de base.");
                }

                string[] parts = workDirectory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    parts = new[] { DefaultWorkDirectory };
                }
                char[] invalidChars = Path.GetInvalidFileNameChars();
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i].Trim();
                    if (part == "." || part == ".." || part.IndexOfAny(invalidChars) >= 0)
                    {
                        throw new InvalidOperationException("Le repertoire de travail OpenCode contient un segment invalide.");
                    }
                    parts[i] = part;
                }

                string target = Path.GetFullPath(Path.Combine(basePath, Path.Combine(parts)));
                if (!IsSameOrChildPath(target, basePath))
                {
                    throw new InvalidOperationException("Le repertoire de travail OpenCode sort du dossier Windows de base.");
                }
                return target;
            }

            private static bool PathsOverlap(string first, string second)
            {
                if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                {
                    return false;
                }
                return IsSameOrChildPath(first, second) || IsSameOrChildPath(second, first);
            }

            private static bool IsSameOrChildPath(string candidate, string parent)
            {
                string childPath = NormalizeDirectoryPath(candidate);
                string parentPath = NormalizeDirectoryPath(parent);
                if (string.Equals(childPath, parentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return childPath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizeDirectoryPath(string path)
            {
                string full = Path.GetFullPath(path ?? "");
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            private static string GetUserProfile()
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile)
                    ? Environment.GetEnvironmentVariable("USERPROFILE") ?? ""
                    : profile;
            }

            private static string WindowsPathToWsl(string path)
            {
                string full = Path.GetFullPath(path ?? "");
                if (full.Length >= 3 && full[1] == ':' && (full[2] == '\\' || full[2] == '/'))
                {
                    char drive = char.ToLowerInvariant(full[0]);
                    string rest = full.Substring(3).Replace('\\', '/');
                    return "/mnt/" + drive + "/" + rest;
                }
                return full.Replace('\\', '/');
            }
        }

        private sealed class WslAgentDetection
        {
            public bool WslFound;
            public bool AgentFound;
            public string WslPath;
            public string Distribution;
            public string CommandPath;
            public string Error;
        }

        private sealed class ProcessCapture
        {
            public bool Started;
            public bool TimedOut;
            public int ExitCode;
            public string Output;
            public string Error;
            public string StartError;
        }

        private sealed class WslCliAgentClient
        {
            private readonly CodingAgentDefinition _agent;
            private readonly WslAgentDetection _wsl;
            private readonly string _workspaceRoot;
            private readonly string _kivrioRoot;
            private readonly string _codexTestRoot;

            public WslCliAgentClient(CodingAgentDefinition agent, WslAgentDetection wsl, string workspaceRoot, string kivrioRoot, string codexTestRoot)
            {
                _agent = agent;
                _wsl = wsl;
                _workspaceRoot = workspaceRoot;
                _kivrioRoot = kivrioRoot;
                _codexTestRoot = codexTestRoot;
            }

            public Dictionary<string, object> RunTurn(string prompt, string systemPrompt, string model, AgentRunProfile profile)
            {
                AgentRunProfile runProfile = profile ?? AgentRunProfile.FromValue(null);
                string requestedModel = LocalAgentConfig.NormalizeModel(model);
                string input = BuildAgentPrompt(prompt, systemPrompt, requestedModel, runProfile);
                ProcessCapture capture = RunAgentProcess(input, requestedModel, runProfile);
                if (!capture.Started)
                {
                    throw new InvalidOperationException(_agent.Label + " n'a pas pu demarrer via WSL: " + (capture.StartError ?? "erreur inconnue."));
                }
                if (capture.TimedOut)
                {
                    throw new TimeoutException("Temps maximal depasse pendant la reponse " + _agent.Label + " via WSL (" + (ReadWslTurnTimeoutMs(runProfile) / 1000) + " secondes).");
                }
                if (capture.ExitCode != 0)
                {
                    throw new InvalidOperationException(_agent.Label + " via WSL a echoue: " + BuildProcessError(capture));
                }

                string answer = ExtractAnswer(capture.Output);
                if (string.IsNullOrWhiteSpace(answer))
                {
                    answer = StripAnsi(capture.Output ?? "").Trim();
                }
                if (string.IsNullOrWhiteSpace(answer))
                {
                    answer = _agent.Label + " n'a pas retourne de texte.";
                }

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "answer", answer },
                    { "reasoning", "" },
                    { "threadId", "" },
                    { "turnId", "" },
                    { "model", requestedModel },
                    { "provider", LocalAgentConfig.Provider },
                    { "agent", _agent.Id },
                    { "agentLabel", _agent.Label },
                    { "providerLabel", LocalAgentConfig.ProviderLabel },
                    { "agentMode", "wsl-cli" },
                    { "requestedModel", requestedModel },
                    { "effectiveModel", requestedModel },
                    { "requestedProvider", LocalAgentConfig.Provider },
                    { "effectiveProvider", LocalAgentConfig.Provider },
                    { "profile", runProfile.Id },
                    { "profileLabel", runProfile.Label },
                    { "turnTimeoutMs", ReadWslTurnTimeoutMs(runProfile) },
                    { "wslDistribution", _wsl.Distribution ?? "" },
                    { "wslCommandPath", _wsl.CommandPath ?? "" },
                    { "workspaceRoot", _workspaceRoot }
                };
            }

            public static string GetCodexTestRoot()
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(documents))
                {
                    documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
                }
                return Path.Combine(documents, "CodexCLI-Test");
            }

            public static string ResolveWorkspaceRoot(string prompt, string kivrioRoot, string codexTestRoot)
            {
                if (PromptTargetsCodexTest(prompt, codexTestRoot))
                {
                    Directory.CreateDirectory(codexTestRoot);
                    return codexTestRoot;
                }
                return kivrioRoot;
            }

            private static bool PromptTargetsCodexTest(string prompt, string codexTestRoot)
            {
                string value = (prompt ?? "").ToLowerInvariant();
                string normalizedRoot = (codexTestRoot ?? "").ToLowerInvariant();
                return value.Contains("codexcli-test")
                    || value.Contains("codexcli test")
                    || (!string.IsNullOrEmpty(normalizedRoot) && value.Contains(normalizedRoot));
            }

            private ProcessCapture RunAgentProcess(string input, string model, AgentRunProfile profile)
            {
                string distro = string.Equals(_wsl.Distribution, "default", StringComparison.OrdinalIgnoreCase) ? "" : (_wsl.Distribution ?? "");
                string promptFile = CreatePromptTempFile(input);
                string scriptFile = "";
                try
                {
                    scriptFile = CreateAgentScriptTempFile(model, promptFile);
                    string wslScriptFile = WindowsPathToWsl(scriptFile);
                    string arguments = string.IsNullOrWhiteSpace(distro)
                        ? "-- bash " + QuoteArg(wslScriptFile)
                        : "-d " + QuoteArg(distro) + " -- bash " + QuoteArg(wslScriptFile);
                    return RunProcessCaptureWithInput(_wsl.WslPath, arguments, "", ReadWslTurnTimeoutMs(profile));
                }
                finally
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(promptFile) && File.Exists(promptFile)) File.Delete(promptFile);
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (!string.IsNullOrEmpty(scriptFile) && File.Exists(scriptFile)) File.Delete(scriptFile);
                    }
                    catch
                    {
                    }
                }
            }

            private string BuildAgentScript(string model, string promptFile)
            {
                string workspace = WindowsPathToWsl(_workspaceRoot);
                string wslPromptFile = WindowsPathToWsl(promptFile);
                string agentPath = _wsl.CommandPath ?? "";
                var builder = new StringBuilder();
                builder.AppendLine("#!/usr/bin/env bash");
                builder.AppendLine("set -u");
                builder.Append("cd ").Append(ShellSingleQuote(workspace)).AppendLine(" || exit 72");
                builder.Append("prompt_file=").Append(ShellSingleQuote(wslPromptFile)).AppendLine();
                builder.AppendLine("if [ ! -f \"$prompt_file\" ]; then");
                builder.AppendLine("  printf '%s\\n' \"Fichier prompt WSL introuvable: $prompt_file\" >&2");
                builder.AppendLine("  exit 73");
                builder.AppendLine("fi");
                builder.AppendLine("prompt=$(cat \"$prompt_file\")");
                builder.AppendLine("rm -f \"$prompt_file\"");
                builder.AppendLine("if [ -z \"$prompt\" ]; then");
                builder.AppendLine("  printf '%s\\n' \"Prompt WSL vide.\" >&2");
                builder.AppendLine("  exit 74");
                builder.AppendLine("fi");

                if (_agent.Id == "opencode")
                {
                    builder.Append("exec ").Append(ShellSingleQuote(agentPath));
                    builder.Append(" run --format json");
                    builder.Append(" --model ").Append(ShellSingleQuote(ToOpenCodeModel(model)));
                    builder.Append(" --dir ").Append(ShellSingleQuote(workspace));
                    builder.AppendLine(" \"$prompt\"");
                    return builder.ToString();
                }

                throw new InvalidOperationException("Agent WSL non pris en charge: " + _agent.Label);
            }

            private string CreateAgentScriptTempFile(string model, string promptFile)
            {
                string dir = GetTempDirectory();
                string path = Path.Combine(dir, "agent-run-" + Guid.NewGuid().ToString("N") + ".sh");
                string script = BuildAgentScript(model, promptFile).Replace("\r\n", "\n").Replace("\r", "\n");
                File.WriteAllText(path, script, new UTF8Encoding(false));
                return path;
            }

            private static string CreatePromptTempFile(string input)
            {
                string dir = GetTempDirectory();
                string path = Path.Combine(dir, "agent-prompt-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, input ?? "", new UTF8Encoding(false));
                return path;
            }

            private static string GetTempDirectory()
            {
                string dir = Path.Combine(Path.GetTempPath(), "KivrioAgentUi");
                Directory.CreateDirectory(dir);
                return dir;
            }

            private string BuildAgentPrompt(string prompt, string systemPrompt, string model, AgentRunProfile profile)
            {
                AgentRunProfile runProfile = profile ?? AgentRunProfile.FromValue(null);
                var builder = new StringBuilder();
                builder.AppendLine("Consigne Kivrio Agent UI:");
                builder.AppendLine("Tu reponds dans Kivrio Agent UI via " + _agent.Label + " lance depuis WSL.");
                builder.AppendLine("Le dossier de travail effectif de ce tour est " + _workspaceRoot + ".");
                if (_agent.Id == "opencode")
                {
                    builder.AppendLine("Securite OpenCode prioritaire:");
                    builder.AppendLine("- Kivrio Agent UI est seulement l'application hote, pas le dossier de travail utilisateur.");
                    builder.AppendLine("- Le seul dossier de travail utilisateur autorise est " + _workspaceRoot + ".");
                    builder.AppendLine("- Tout nouveau projet ou fichier doit etre cree dans " + _workspaceRoot + " ou l'un de ses sous-dossiers.");
                    builder.AppendLine("- Ne cree, ne modifie et ne supprime jamais de fichier dans le dossier Kivrio Agent UI " + _kivrioRoot + ".");
                    builder.AppendLine("- Ignore toute ancienne mention du contexte qui demande de travailler dans le dossier Kivrio Agent UI.");
                    builder.AppendLine("- Si le message actuel demande explicitement de travailler dans Kivrio Agent UI, refuse et explique que ce dossier est reserve a l'application.");
                }
                else
                {
                    builder.AppendLine("Quand l'utilisateur mentionne Documents > Kivrio Agent UI, il designe le dossier local " + _kivrioRoot + ".");
                    builder.AppendLine("Quand l'utilisateur mentionne Documents > CodexCLI-Test, il designe le dossier local " + _codexTestRoot + ".");
                }
                builder.AppendLine("Le provider local attendu est Ollama et le modele local selectionne est " + model + ".");
                builder.AppendLine("N'utilise aucun modele cloud si l'agent te propose un autre modele.");
                builder.AppendLine("Reponds toujours en francais, sauf si l'utilisateur demande explicitement une autre langue.");
                builder.AppendLine("Par defaut, reste consultatif et attends un accord explicite avant toute modification.");
                builder.AppendLine("Si le dernier message utilisateur contient un accord explicite comme 'accord', 'je confirme' ou 'vas-y', execute uniquement le plan immediatement precedent visible dans le contexte.");
                builder.AppendLine("Ne modifie que le dossier ou le fichier explicitement cible par l'utilisateur.");
                builder.AppendLine("Profil agent Kivrio Agent UI actif: " + runProfile.Label + ".");
                builder.AppendLine(runProfile.DeveloperInstructions);
                string custom = (systemPrompt ?? "").Trim();
                if (!string.IsNullOrEmpty(custom))
                {
                    builder.AppendLine();
                    builder.AppendLine("Instructions utilisateur de Kivrio Agent UI:");
                    builder.AppendLine(custom);
                }
                builder.AppendLine();
                builder.AppendLine("Message utilisateur:");
                builder.Append(prompt ?? "");
                return builder.ToString();
            }

            private string ExtractAnswer(string output)
            {
                string text = output ?? "";
                if (_agent.Id != "opencode")
                {
                    return StripAnsi(text).Trim();
                }

                var parser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var builder = new StringBuilder();
                string normalized = text.Replace("\r", "");
                string[] lines = normalized.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = StripAnsi(lines[i]).Trim();
                    if (string.IsNullOrEmpty(line) || line[0] != '{') continue;
                    try
                    {
                        Dictionary<string, object> item = parser.DeserializeObject(line) as Dictionary<string, object>;
                        if (item == null) continue;
                        string type = GetStringValue(item, "type");
                        Dictionary<string, object> part = GetDictionaryValue(item, "part");
                        string partType = GetStringValue(part, "type");
                        string content = GetStringValue(part, "text");
                        if ((type == "text" || partType == "text") && !string.IsNullOrEmpty(content))
                        {
                            builder.Append(content);
                        }
                    }
                    catch
                    {
                    }
                }

                if (builder.Length > 0)
                {
                    return builder.ToString().Trim();
                }
                return StripAnsi(text).Trim();
            }

            private static string ToOpenCodeModel(string model)
            {
                string value = (model ?? "").Trim();
                if (value.IndexOf("/", StringComparison.OrdinalIgnoreCase) >= 0) return value;
                return "ollama/" + value;
            }

            private static string WindowsPathToWsl(string path)
            {
                string full = "";
                try
                {
                    full = Path.GetFullPath(path ?? "");
                }
                catch
                {
                    full = path ?? "";
                }
                if (full.Length >= 3 && full[1] == ':' && (full[2] == '\\' || full[2] == '/'))
                {
                    char drive = char.ToLowerInvariant(full[0]);
                    string rest = full.Substring(3).Replace('\\', '/');
                    return "/mnt/" + drive + "/" + rest;
                }
                return full.Replace('\\', '/');
            }

            private static string BuildProcessError(ProcessCapture capture)
            {
                string error = StripAnsi(((capture.Error ?? "") + "\n" + (capture.Output ?? "")).Trim());
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "code de sortie " + capture.ExitCode + ".";
                }
                return error.Trim();
            }

            private static Dictionary<string, object> GetDictionaryValue(Dictionary<string, object> dict, string key)
            {
                if (dict == null) return new Dictionary<string, object>();
                object value;
                return dict.TryGetValue(key, out value) && value is Dictionary<string, object>
                    ? (Dictionary<string, object>)value
                    : new Dictionary<string, object>();
            }

            private static string GetStringValue(Dictionary<string, object> dict, string key)
            {
                if (dict == null) return "";
                object value;
                return dict.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
            }

            private static string StripAnsi(string value)
            {
                return Regex.Replace(value ?? "", "\x1B\\[[0-?]*[ -/]*[@-~]", "");
            }

            private static int ReadWslTurnTimeoutMs(AgentRunProfile profile)
            {
                string secondsValue = (Environment.GetEnvironmentVariable("KIVRIO_WSL_AGENT_TIMEOUT_SECONDS") ?? "").Trim();
                int seconds;
                if (int.TryParse(secondsValue, out seconds) && seconds > 0)
                {
                    return Clamp(seconds * 1000, 60000, 3600000);
                }
                return Clamp((profile ?? AgentRunProfile.FromValue(null)).DefaultTimeoutMs, 60000, 3600000);
            }

            private static int Clamp(int value, int min, int max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }
        }

        private static WslAgentDetection DetectWslAgent(CodingAgentDefinition agent)
        {
            var detection = new WslAgentDetection();
            string wslPath = FindWslCommand();
            detection.WslPath = wslPath ?? "";
            if (string.IsNullOrEmpty(wslPath) || !File.Exists(wslPath))
            {
                detection.Error = "wsl.exe introuvable.";
                return detection;
            }

            string command = (agent.WslCommand ?? "").Trim();
            string fallback = (agent.WslFallbackPath ?? "").Trim();
            if (string.IsNullOrEmpty(command) && string.IsNullOrEmpty(fallback))
            {
                detection.Error = "Aucune commande WSL declaree pour " + agent.Label + ".";
                return detection;
            }

            string script = BuildWslProbeScript(command, fallback);
            string configuredDistro = (Environment.GetEnvironmentVariable("KIVRIO_AGENT_WSL_DISTRO") ?? "").Trim();
            string[] distros = string.IsNullOrEmpty(configuredDistro)
                ? new[] { "", "Ubuntu" }
                : new[] { configuredDistro, "", "Ubuntu" };

            var tried = new List<string>();
            for (int i = 0; i < distros.Length; i++)
            {
                string distro = distros[i] ?? "";
                string key = string.IsNullOrEmpty(distro) ? "<default>" : distro;
                bool alreadyTried = false;
                for (int j = 0; j < tried.Count; j++)
                {
                    if (string.Equals(tried[j], key, StringComparison.OrdinalIgnoreCase)) alreadyTried = true;
                }
                if (alreadyTried) continue;
                tried.Add(key);

                ProcessCapture result = RunWslProbe(wslPath, distro, script, 2500);
                if (!result.Started)
                {
                    detection.Error = result.StartError;
                    continue;
                }

                detection.WslFound = true;
                detection.Distribution = string.IsNullOrEmpty(distro) ? "default" : distro;
                if (result.TimedOut)
                {
                    detection.Error = "Detection WSL interrompue par timeout.";
                    continue;
                }

                string output = (result.Output ?? "").Trim();
                if (result.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    string[] lines = output.Replace("\r", "").Split('\n');
                    detection.AgentFound = true;
                    detection.CommandPath = lines[0].Trim();
                    detection.Error = "";
                    return detection;
                }

                string error = ((result.Error ?? "") + "\n" + output).Trim();
                if (!string.IsNullOrEmpty(error)) detection.Error = error;
            }

            if (string.IsNullOrEmpty(detection.Error))
            {
                detection.Error = "Commande agent introuvable dans WSL.";
            }
            return detection;
        }

        private static string BuildWslProbeScript(string command, string fallback)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(command))
            {
                parts.Add("command -v " + ShellSingleQuote(command) + " 2>/dev/null");
            }
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                parts.Add("{ test -x " + ShellDoubleQuote(fallback) + " && printf '%s\\n' " + ShellDoubleQuote(fallback) + "; }");
            }
            return string.Join(" || ", parts.ToArray());
        }

        private static ProcessCapture RunWslProbe(string wslPath, string distro, string script, int timeoutMs)
        {
            string arguments = string.IsNullOrWhiteSpace(distro)
                ? "-- bash -lc " + QuoteArg(script)
                : "-d " + QuoteArg(distro) + " -- bash -lc " + QuoteArg(script);
            return RunProcessCapture(wslPath, arguments, timeoutMs);
        }

        private static ProcessCapture RunProcessCapture(string fileName, string arguments, int timeoutMs)
        {
            var capture = new ProcessCapture { ExitCode = -1, Output = "", Error = "" };
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(info))
                {
                    capture.Started = process != null;
                    if (process == null) return capture;
                    if (!process.WaitForExit(timeoutMs))
                    {
                        capture.TimedOut = true;
                        try { process.Kill(); } catch { }
                    }
                    else
                    {
                        capture.ExitCode = process.ExitCode;
                    }
                    capture.Output = process.StandardOutput.ReadToEnd();
                    capture.Error = process.StandardError.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                capture.StartError = ex.Message;
            }
            return capture;
        }

        private static ProcessCapture RunProcessCaptureWithInput(string fileName, string arguments, string input, int timeoutMs)
        {
            var capture = new ProcessCapture { ExitCode = -1, Output = "", Error = "" };
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                try
                {
                    info.StandardOutputEncoding = Encoding.UTF8;
                    info.StandardErrorEncoding = Encoding.UTF8;
                }
                catch
                {
                }

                using (Process process = Process.Start(info))
                {
                    capture.Started = process != null;
                    if (process == null) return capture;

                    string output = "";
                    string error = "";
                    var stdout = new Thread((ThreadStart)delegate
                    {
                        try { output = process.StandardOutput.ReadToEnd(); } catch { }
                    });
                    var stderr = new Thread((ThreadStart)delegate
                    {
                        try { error = process.StandardError.ReadToEnd(); } catch { }
                    });
                    stdout.IsBackground = true;
                    stderr.IsBackground = true;
                    stdout.Start();
                    stderr.Start();

                    try
                    {
                        process.StandardInput.Write(input ?? "");
                        process.StandardInput.Close();
                    }
                    catch
                    {
                    }

                    if (!process.WaitForExit(timeoutMs))
                    {
                        capture.TimedOut = true;
                        try { process.Kill(); } catch { }
                    }
                    else
                    {
                        capture.ExitCode = process.ExitCode;
                    }

                    stdout.Join(2000);
                    stderr.Join(2000);
                    capture.Output = output ?? "";
                    capture.Error = error ?? "";
                }
            }
            catch (Exception ex)
            {
                capture.StartError = ex.Message;
            }
            return capture;
        }

        private static string FindWslCommand()
        {
            string configured = (Environment.GetEnvironmentVariable("KIVRIO_WSL_PATH") ?? "").Trim();
            if (File.Exists(configured)) return configured;

            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string[] candidates = new[]
            {
                Path.Combine(system, "wsl.exe"),
                FindOnPath("wsl.exe")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        private static string ShellSingleQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
        }

        private static string ShellDoubleQuote(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private void EnsureStartedLocked()
        {
            EnsureStartedLocked(LocalAgentConfig.ReadDefaultModel());
        }

        private void EnsureStartedLocked(string model)
        {
            string selectedModel = LocalAgentConfig.NormalizeModel(model);
            if (_port > 0 && ProbeHttp("http://127.0.0.1:" + _port + "/readyz", 500))
            {
                if (string.Equals(_processModel, selectedModel, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                StopProcessLocked();
            }

            if (_process != null && !_process.HasExited)
            {
                if (string.Equals(_processModel, selectedModel, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                StopProcessLocked();
            }

            _process = null;
            _attachedToExisting = false;
            _lastError = null;

            _launcherPath = FindOllamaCommand();
            if (string.IsNullOrEmpty(_launcherPath) || !File.Exists(_launcherPath))
            {
                _lastError = "Ollama introuvable.";
                return;
            }

            _codexPath = FindCodexCommandForOllamaLaunch();
            if (string.IsNullOrEmpty(_codexPath) || !File.Exists(_codexPath))
            {
                _lastError = "Codex CLI executable par Ollama introuvable.";
                return;
            }

            int preferredPort = ReadPort();
            if (EnvFlag("KIVRIO_AGENT_ATTACH_EXISTING", false)
                && ProbeHttp("http://127.0.0.1:" + preferredPort + "/readyz", 500)
                && ProbeHttp("http://127.0.0.1:" + preferredPort + "/healthz", 500))
            {
                _port = preferredPort;
                _attachedToExisting = true;
                _processModel = selectedModel;
                return;
            }

            _port = FindAvailablePort(preferredPort);
            if (_port <= 0)
            {
                _lastError = "Aucun port local disponible pour codex app-server.";
                return;
            }

            try
            {
                ProcessStartInfo info = BuildStartInfo(_launcherPath, _codexPath, _port, _root, selectedModel);
                _process = Process.Start(info);
                _processModel = selectedModel;
                _startedAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _process = null;
                _processModel = null;
                _lastError = "Demarrage de Codex CLI impossible: " + ex.Message;
                return;
            }

            if (!WaitUntilReady(_port, _process, 8000))
            {
                if (_process != null && _process.HasExited)
                {
                    _lastError = "codex app-server s'est arrete pendant le demarrage.";
                }
                else
                {
                    _lastError = "codex app-server ne repond pas encore.";
                }
            }
        }

        private void StopProcessLocked()
        {
            if (_process == null || _process.HasExited)
            {
                _process = null;
                _attachedToExisting = false;
                _processModel = null;
                _port = 0;
                return;
            }

            try
            {
                KillProcessTree(_process.Id);
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                _process = null;
                _attachedToExisting = false;
                _processModel = null;
                _port = 0;
            }
        }

        private static void KillProcessTree(int processId)
        {
            try
            {
                string taskkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                if (!File.Exists(taskkill))
                {
                    return;
                }
                var info = new ProcessStartInfo
                {
                    FileName = taskkill,
                    Arguments = "/PID " + processId + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(info))
                {
                    if (process != null)
                    {
                        process.WaitForExit(2000);
                    }
                }
            }
            catch
            {
            }
        }

        private static ProcessStartInfo BuildStartInfo(string ollamaPath, string codexPath, int port, string root, string model)
        {
            string arguments = BuildOllamaLaunchArguments(port, model);
            var info = new ProcessStartInfo();

            info.FileName = ollamaPath;
            info.Arguments = arguments;

            info.WorkingDirectory = Directory.Exists(root) ? root : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.EnvironmentVariables["PATH"] = BuildOllamaLaunchPath(codexPath);
            return info;
        }

        private static string BuildOllamaLaunchArguments(int port, string selectedModel)
        {
            var parts = new List<string>();
            LocalAgentConfig.ReadProvider();
            string model = LocalAgentConfig.NormalizeModel(selectedModel);
            parts.Add("launch");
            parts.Add("codex");
            parts.Add("--model " + QuoteArg(model));
            parts.Add("--yes");
            parts.Add("--");
            parts.Add("app-server --listen ws://127.0.0.1:" + port);
            string arguments = string.Join(" ", parts.ToArray());
            GuardOllamaLaunchArguments(arguments);
            return arguments;
        }

        private static void GuardOllamaLaunchArguments(string arguments)
        {
            string value = " " + (arguments ?? "") + " ";
            if (value.IndexOf(" launch ", StringComparison.OrdinalIgnoreCase) < 0
                || value.IndexOf(" codex ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(LocalAgentConfig.RefuseNonOssModeMessage);
            }
            if (value.IndexOf(" --model ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(LocalAgentConfig.MissingModelMessage);
            }
            if (value.IndexOf(" -- ", StringComparison.OrdinalIgnoreCase) < 0
                || value.IndexOf(" app-server ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("Mode refuse : Kivrio Agent UI doit lancer Codex en app-server via Ollama.");
            }
        }

        private static string GetCodexJsForWrapper(string codexWrapperPath)
        {
            string dir = Path.GetDirectoryName(codexWrapperPath) ?? "";
            return Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
        }

        private static string FindNodeExecutable(string preferredDir)
        {
            if (!string.IsNullOrEmpty(preferredDir))
            {
                string bundled = Path.Combine(preferredDir, "node.exe");
                if (File.Exists(bundled)) return bundled;
            }
            return FindOnPath("node.exe");
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string FindOllamaCommand()
        {
            string configured = (Environment.GetEnvironmentVariable("KIVRIO_OLLAMA_PATH") ?? "").Trim();
            if (File.Exists(configured)) return configured;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"),
                FindOnPath("ollama.exe")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        private static string FindCodexCommandForOllamaLaunch()
        {
            string configured = (Environment.GetEnvironmentVariable("KIVRIO_CODEX_PATH") ?? "").Trim();
            if (File.Exists(configured)) return configured;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] candidates = new[]
            {
                Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe"),
                Path.Combine(appData, "npm", "codex.cmd"),
                Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe"),
                FindOnPathOutsideWindowsApps("codex.cmd"),
                FindOnPathOutsideWindowsApps("codex.exe"),
                FindOnPathOutsideWindowsApps("codex")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        private static string BuildOllamaLaunchPath(string codexPath)
        {
            var dirs = new List<string>();
            AddPathDir(dirs, Path.GetDirectoryName(codexPath));
            AddPathDir(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", ".sandbox-bin"));
            AddPathDir(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            AddPathDir(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"));

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] existing = path.Split(Path.PathSeparator);
            for (int i = 0; i < existing.Length; i++)
            {
                AddPathDir(dirs, existing[i]);
            }
            return string.Join(Path.PathSeparator.ToString(), dirs.ToArray());
        }

        private static void AddPathDir(List<string> dirs, string dir)
        {
            string value = (dir ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(value) || !Directory.Exists(value))
            {
                return;
            }
            for (int i = 0; i < dirs.Count; i++)
            {
                if (string.Equals(dirs[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            dirs.Add(value);
        }

        private static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] dirs = path.Split(Path.PathSeparator);
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = (dirs[i] ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        private static string FindOnPathOutsideWindowsApps(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] dirs = path.Split(Path.PathSeparator);
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = (dirs[i] ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(dir) || IsWindowsAppsPath(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool IsWindowsAppsPath(string path)
        {
            return (path ?? "").IndexOf("\\WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ReadPort()
        {
            string raw = Environment.GetEnvironmentVariable("KIVRIO_AGENT_CODEX_PORT");
            int port;
            if (int.TryParse(raw, out port) && port > 0 && port < 65536)
            {
                return port;
            }
            return DefaultPort;
        }

        private static bool EnvFlag(string name, bool fallback)
        {
            string value = (Environment.GetEnvironmentVariable(name) ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(value)) return fallback;
            return value == "1" || value == "true" || value == "yes" || value == "on";
        }

        private static int FindAvailablePort(int preferredPort)
        {
            for (int offset = 0; offset < MaxPortScan; offset++)
            {
                int port = preferredPort + offset;
                if (port > 0 && port < 65536 && IsPortAvailable(port))
                {
                    return port;
                }
            }
            return 0;
        }

        private static bool IsPortAvailable(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Stop(); } catch { }
                }
            }
        }

        private static bool WaitUntilReady(int port, Process process, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (process != null && process.HasExited)
                {
                    return false;
                }
                if (ProbeHttp("http://127.0.0.1:" + port + "/readyz", 500)
                    && ProbeHttp("http://127.0.0.1:" + port + "/healthz", 500))
                {
                    return true;
                }
                Thread.Sleep(250);
            }
            return false;
        }

        private static bool ProbeHttp(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    return code >= 200 && code < 300;
                }
            }
            catch
            {
                return false;
            }
        }

        private static long UnixTimeSeconds(DateTime value)
        {
            return (long)(value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }

    internal sealed class CodexAppServerClient
    {
        private const int DefaultTurnTimeoutMs = 1200000;
        private const int MinTurnTimeoutMs = 60000;
        private const int MaxTurnTimeoutMs = 3600000;
        private readonly int _port;
        private readonly string _root;
        private readonly JavaScriptSerializer _json;
        private int _nextId;

        public CodexAppServerClient(int port, string root)
        {
            _port = port;
            _root = root;
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        public Dictionary<string, object> RunTurn(string prompt, string systemPrompt, string model, AgentRunProfile profile)
        {
            AgentRunProfile runProfile = profile ?? AgentRunProfile.FromValue(null);
            int turnTimeoutMs = ReadTurnTimeoutMs(runProfile);
            string codexTestRoot = GetCodexTestRoot();
            string workspaceRoot = ResolveWorkspaceRoot(prompt, codexTestRoot);
            object[] writableRoots = BuildWritableRoots(workspaceRoot, codexTestRoot);
            bool useExecutableSandbox = SamePath(workspaceRoot, codexTestRoot);

            using (var socket = new ClientWebSocket())
            using (var timeout = new CancellationTokenSource(turnTimeoutMs))
            {
                try
                {
                    socket.ConnectAsync(new Uri("ws://127.0.0.1:" + _port), timeout.Token).GetAwaiter().GetResult();
                    Initialize(socket, timeout.Token);

                    string requestedModel = NormalizeSelectedLocalModel(model);
                    string requestedProvider = LocalAgentConfig.ReadProvider();
                    Dictionary<string, object> thread = Request(socket, "thread/start", BuildThreadParams(systemPrompt, requestedModel, requestedProvider, workspaceRoot, codexTestRoot, writableRoots, useExecutableSandbox, runProfile), timeout.Token, null);
                    string threadId = GetNestedString(thread, "thread", "id");
                    if (string.IsNullOrEmpty(threadId))
                    {
                        throw new InvalidOperationException("thread/start n'a pas retourne d'identifiant de thread.");
                    }

                    string effectiveModel = NormalizeEffectiveModel(GetString(thread, "model"), requestedModel);
                    string effectiveProvider = NormalizeEffectiveProvider(GetString(thread, "modelProvider"), requestedProvider);

                    var capture = new CodexTurnCapture(threadId, workspaceRoot, writableRoots, useExecutableSandbox);
                    Dictionary<string, object> turn = Request(socket, "turn/start", BuildTurnParams(threadId, BuildPromptForTurn(prompt, runProfile), requestedModel, requestedProvider, workspaceRoot, writableRoots, useExecutableSandbox), timeout.Token, capture);
                    string turnId = GetNestedString(turn, "turn", "id");
                    if (!string.IsNullOrEmpty(turnId))
                    {
                        capture.TurnId = turnId;
                    }

                    while (!capture.Completed)
                    {
                        Dictionary<string, object> message = ReceiveJson(socket, timeout.Token);
                        HandleIncoming(socket, message, timeout.Token, capture);
                    }

                    string answer = capture.Answer.ToString().Trim();
                    string reasoning = capture.Reasoning.ToString().Trim();
                    if (string.IsNullOrEmpty(answer) && !string.IsNullOrEmpty(capture.CompletedAgentMessage))
                    {
                        answer = capture.CompletedAgentMessage.Trim();
                    }
                    if (string.IsNullOrEmpty(reasoning) && !string.IsNullOrEmpty(capture.CompletedReasoning))
                    {
                        reasoning = capture.CompletedReasoning.Trim();
                    }
                    if (capture.ToolNotes.Length > 0)
                    {
                        if (!string.IsNullOrEmpty(reasoning)) reasoning += "\n\n";
                        reasoning += capture.ToolNotes.ToString().Trim();
                    }
                    if (string.IsNullOrEmpty(answer) && !string.IsNullOrEmpty(capture.Error))
                    {
                        throw new InvalidOperationException(capture.Error);
                    }
                    if (string.IsNullOrEmpty(answer))
                    {
                        answer = "Codex CLI n'a pas retourne de texte.";
                    }

                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "answer", answer },
                        { "reasoning", reasoning },
                        { "threadId", threadId },
                        { "turnId", capture.TurnId ?? "" },
                        { "model", effectiveModel },
                        { "provider", effectiveProvider },
                        { "agent", LocalAgentConfig.Agent },
                        { "agentLabel", LocalAgentConfig.AgentLabel },
                        { "providerLabel", LocalAgentConfig.ProviderLabel },
                        { "agentMode", LocalAgentConfig.Mode },
                        { "requestedModel", requestedModel },
                        { "effectiveModel", effectiveModel },
                        { "requestedProvider", requestedProvider },
                        { "effectiveProvider", effectiveProvider },
                        { "profile", runProfile.Id },
                        { "profileLabel", runProfile.Label },
                        { "turnTimeoutMs", turnTimeoutMs }
                    };
                }
                catch (OperationCanceledException ex)
                {
                    throw new TimeoutException("Temps maximal depasse pendant la reponse Codex/Ollama (" + (turnTimeoutMs / 1000) + " secondes).", ex);
                }
            }
        }

        private static int ReadTurnTimeoutMs(AgentRunProfile profile)
        {
            string secondsValue = (Environment.GetEnvironmentVariable("KIVRIO_CODEX_TURN_TIMEOUT_SECONDS") ?? "").Trim();
            int seconds;
            if (int.TryParse(secondsValue, out seconds) && seconds > 0)
            {
                return Clamp(seconds * 1000, MinTurnTimeoutMs, MaxTurnTimeoutMs);
            }

            string msValue = (Environment.GetEnvironmentVariable("KIVRIO_CODEX_TURN_TIMEOUT_MS") ?? "").Trim();
            int milliseconds;
            if (int.TryParse(msValue, out milliseconds) && milliseconds > 0)
            {
                return Clamp(milliseconds, MinTurnTimeoutMs, MaxTurnTimeoutMs);
            }

            return Clamp((profile ?? AgentRunProfile.FromValue(null)).DefaultTimeoutMs, MinTurnTimeoutMs, MaxTurnTimeoutMs);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void Initialize(ClientWebSocket socket, CancellationToken token)
        {
            var clientInfo = new Dictionary<string, object>
            {
                { "name", "kivrio-agent-ui" },
                { "title", "Kivrio Agent UI" },
                { "version", "2026.5.2" }
            };
            var capabilities = new Dictionary<string, object>
            {
                { "experimentalApi", true }
            };
            var parameters = new Dictionary<string, object>
            {
                { "clientInfo", clientInfo },
                { "capabilities", capabilities }
            };

            Request(socket, "initialize", parameters, token, null);
            SendJson(socket, new Dictionary<string, object> { { "method", "initialized" } }, token);
        }

        private string ResolveWorkspaceRoot(string prompt, string codexTestRoot)
        {
            if (PromptTargetsCodexTest(prompt, codexTestRoot))
            {
                Directory.CreateDirectory(codexTestRoot);
                return codexTestRoot;
            }
            return _root;
        }

        private static bool PromptTargetsCodexTest(string prompt, string codexTestRoot)
        {
            string value = (prompt ?? "").ToLowerInvariant();
            string normalizedRoot = (codexTestRoot ?? "").ToLowerInvariant();
            return value.Contains("codexcli-test")
                || value.Contains("codexcli test")
                || (!string.IsNullOrEmpty(normalizedRoot) && value.Contains(normalizedRoot));
        }

        private static object[] BuildWritableRoots(string workspaceRoot, string codexTestRoot)
        {
            var roots = new List<object>();
            AddUniqueRoot(roots, workspaceRoot);
            AddUniqueRoot(roots, codexTestRoot);
            return roots.ToArray();
        }

        private static void AddUniqueRoot(List<object> roots, string path)
        {
            string normalized = NormalizeFullPath(path);
            if (string.IsNullOrEmpty(normalized)) return;
            foreach (object existing in roots)
            {
                if (SamePath(Convert.ToString(existing), normalized)) return;
            }
            roots.Add(normalized);
        }

        private Dictionary<string, object> BuildThreadParams(string systemPrompt, string model, string provider, string workspaceRoot, string codexTestRoot, object[] writableRoots, bool useExecutableSandbox, AgentRunProfile profile)
        {
            var parameters = new Dictionary<string, object>
            {
                { "cwd", workspaceRoot },
                { "approvalPolicy", "on-request" },
                { "sandbox", useExecutableSandbox ? "danger-full-access" : "workspace-write" },
                { "config", BuildThreadConfig(writableRoots) },
                { "ephemeral", true },
                { "experimentalRawEvents", false },
                { "persistExtendedHistory", false },
                { "developerInstructions", BuildDeveloperInstructions(systemPrompt, model, _root, codexTestRoot, workspaceRoot, profile) }
            };
            parameters["model"] = model;
            parameters["modelProvider"] = provider;
            return parameters;
        }

        private static Dictionary<string, object> BuildThreadConfig(object[] writableRoots)
        {
            return new Dictionary<string, object>
            {
                {
                    "sandbox_workspace_write",
                    new Dictionary<string, object>
                    {
                        { "network_access", false },
                        { "writable_roots", writableRoots }
                    }
                }
            };
        }

        private static Dictionary<string, object> BuildSandboxPolicy(object[] writableRoots, bool useExecutableSandbox)
        {
            if (useExecutableSandbox)
            {
                return new Dictionary<string, object>
                {
                    { "type", "dangerFullAccess" }
                };
            }

            return new Dictionary<string, object>
            {
                { "type", "workspaceWrite" },
                { "networkAccess", false },
                { "writableRoots", writableRoots },
                { "readOnlyAccess", new Dictionary<string, object> { { "type", "fullAccess" } } }
            };
        }

        private static string GetCodexTestRoot()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            }
            return Path.Combine(documents, "CodexCLI-Test");
        }

        private static string BuildDeveloperInstructions(string systemPrompt, string model, string root, string codexTestRoot, string workspaceRoot, AgentRunProfile profile)
        {
            AgentRunProfile runProfile = profile ?? AgentRunProfile.FromValue(null);
            var builder = new StringBuilder();
            builder.Append("Tu reponds dans Kivrio Agent UI via Codex CLI. ");
            builder.Append("Le dossier de travail effectif de ce tour est ");
            builder.Append(workspaceRoot);
            builder.Append(". ");
            builder.Append("Quand l'utilisateur mentionne Documents > Kivrio Agent UI, il designe le dossier de travail local ");
            builder.Append(root);
            builder.Append(". ");
            builder.Append("Quand l'utilisateur mentionne Documents > CodexCLI-Test, il designe le dossier de test local ");
            builder.Append(codexTestRoot);
            builder.Append(". ");
            builder.Append("Le canal est Codex CLI/app-server, l'agent est Codex CLI, le provider local est Ollama et le modele local selectionne est ");
            builder.Append(LocalAgentConfig.NormalizeModel(model));
            builder.Append(". ");
            builder.Append("Le profil agent Kivrio Agent UI actif est ");
            builder.Append(runProfile.Label);
            builder.Append(". ");
            builder.Append(runProfile.DeveloperInstructions);
            builder.Append("Reponds toujours en francais, sauf si l'utilisateur demande explicitement une autre langue. ");
            builder.Append("Les explications, diagnostics, plans et messages visibles doivent etre en francais. ");
            builder.Append("Par defaut, reste consultatif: propose un plan court et attends un accord explicite avant toute modification. ");
            builder.Append("Si le dernier message utilisateur contient un accord explicite comme 'accord', 'je confirme' ou 'vas-y', tu peux executer uniquement le plan immediatement precedent visible dans le contexte de conversation. ");
            builder.Append("Ne modifie que le dossier ou le fichier explicitement cible par l'utilisateur. ");
            builder.Append("Ne modifie jamais le dossier Kivrio Agent UI sauf si l'utilisateur le demande explicitement comme dossier cible. ");
            builder.Append("Pour une creation ou un test hors projet, utilise Documents > CodexCLI-Test uniquement si l'utilisateur l'a nomme explicitement. ");
            builder.Append("Si le fichier ou le dossier cible n'est pas clair, demande une clarification au lieu d'agir. ");
            builder.Append("N'installe aucune dependance et ne lance aucun build sans accord explicite. ");
            builder.Append("Apres action, resume les fichiers crees ou modifies.");
            string custom = (systemPrompt ?? "").Trim();
            if (!string.IsNullOrEmpty(custom))
            {
                builder.Append("\n\nInstructions utilisateur de Kivrio Agent UI:\n");
                builder.Append(custom);
            }
            return builder.ToString();
        }

        private static string BuildPromptForTurn(string prompt, AgentRunProfile profile)
        {
            AgentRunProfile runProfile = profile ?? AgentRunProfile.FromValue(null);
            var builder = new StringBuilder();
            builder.AppendLine("Consigne Kivrio Agent UI:");
            builder.AppendLine("Reponds en francais. N'utilise l'anglais que si l'utilisateur le demande explicitement.");
            builder.AppendLine(runProfile.TurnInstructions);
            builder.AppendLine();
            builder.AppendLine("Message utilisateur:");
            builder.Append(prompt ?? "");
            return builder.ToString();
        }

        private static Dictionary<string, object> BuildTurnParams(string threadId, string prompt, string model, string provider, string workspaceRoot, object[] writableRoots, bool useExecutableSandbox)
        {
            var inputItem = new Dictionary<string, object>
            {
                { "type", "text" },
                { "text", prompt ?? "" },
                { "text_elements", new object[0] }
            };
            var parameters = new Dictionary<string, object>
            {
                { "threadId", threadId },
                { "cwd", workspaceRoot },
                { "input", new object[] { inputItem } },
                { "approvalPolicy", "on-request" },
                { "sandboxPolicy", BuildSandboxPolicy(writableRoots, useExecutableSandbox) },
                { "model", model },
                { "modelProvider", provider }
            };
            return parameters;
        }

        private static string NormalizeSelectedLocalModel(string model)
        {
            string value = (model ?? "").Trim();
            if (string.IsNullOrEmpty(value))
            {
                value = LocalAgentConfig.ReadDefaultModel();
            }
            return LocalAgentConfig.NormalizeModel(value);
        }

        private static string NormalizeEffectiveModel(string value, string fallback)
        {
            string model = (value ?? "").Trim();
            if (string.IsNullOrEmpty(model)) model = fallback;
            if (!string.Equals(model, fallback, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Modele Codex inattendu: " + model);
            }
            return fallback;
        }

        private static string NormalizeEffectiveProvider(string value, string fallback)
        {
            string provider = (value ?? "").Trim();
            if (string.IsNullOrEmpty(provider)) provider = fallback;
            if (!string.Equals(provider, LocalAgentConfig.Provider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Provider Codex inattendu: " + provider);
            }
            return LocalAgentConfig.Provider;
        }

        private Dictionary<string, object> Request(ClientWebSocket socket, string method, Dictionary<string, object> parameters, CancellationToken token, CodexTurnCapture capture)
        {
            int id = Interlocked.Increment(ref _nextId);
            var request = new Dictionary<string, object>
            {
                { "id", id },
                { "method", method },
                { "params", parameters ?? new Dictionary<string, object>() }
            };
            SendJson(socket, request, token);

            while (true)
            {
                Dictionary<string, object> message = ReceiveJson(socket, token);
                object responseId;
                if (message.TryGetValue("id", out responseId) && SameId(responseId, id))
                {
                    object error;
                    if (message.TryGetValue("error", out error))
                    {
                        throw new InvalidOperationException(JsonRpcErrorMessage(error));
                    }

                    object result;
                    if (message.TryGetValue("result", out result))
                    {
                        return AsDictionary(result);
                    }
                    return new Dictionary<string, object>();
                }

                HandleIncoming(socket, message, token, capture);
            }
        }

        private void HandleIncoming(ClientWebSocket socket, Dictionary<string, object> message, CancellationToken token, CodexTurnCapture capture)
        {
            string method = GetString(message, "method");
            if (string.IsNullOrEmpty(method))
            {
                return;
            }

            object requestId;
            if (message.TryGetValue("id", out requestId))
            {
                Dictionary<string, object> requestParameters = AsDictionary(GetObject(message, "params"));
                SendJson(socket, BuildServerRequestResponse(requestId, method, requestParameters, capture), token);
                return;
            }

            if (capture == null)
            {
                return;
            }

            Dictionary<string, object> parameters = AsDictionary(GetObject(message, "params"));
            if (method == "item/agentMessage/delta")
            {
                capture.Answer.Append(GetString(parameters, "delta"));
                return;
            }
            if (method == "item/reasoning/textDelta" || method == "item/reasoning/summaryTextDelta")
            {
                capture.Reasoning.Append(GetString(parameters, "delta"));
                return;
            }
            if (method == "item/completed")
            {
                CaptureCompletedItem(capture, parameters);
                return;
            }
            if (method == "turn/completed")
            {
                capture.Completed = true;
                capture.Error = ExtractTurnError(parameters);
            }
        }

        private static Dictionary<string, object> BuildServerRequestResponse(object requestId, string method, Dictionary<string, object> parameters, CodexTurnCapture capture)
        {
            if (method == "item/commandExecution/requestApproval")
            {
                bool approved = IsSafeCommandApproval(parameters, capture);
                return new Dictionary<string, object>
                {
                    { "id", requestId },
                    { "result", new Dictionary<string, object> { { "decision", approved ? "accept" : "decline" } } }
                };
            }
            if (method == "item/fileChange/requestApproval")
            {
                bool approved = IsSafeFileChangeApproval(parameters, capture);
                return new Dictionary<string, object>
                {
                    { "id", requestId },
                    { "result", new Dictionary<string, object> { { "decision", approved ? "accept" : "decline" } } }
                };
            }
            if (method == "item/permissions/requestApproval")
            {
                return new Dictionary<string, object>
                {
                    { "id", requestId },
                    { "result", new Dictionary<string, object>
                        {
                            { "permissions", BuildGrantedPermissions(parameters, capture) },
                            { "scope", "turn" }
                        }
                    }
                };
            }

            return new Dictionary<string, object>
            {
                { "id", requestId },
                { "error", new Dictionary<string, object>
                    {
                        { "code", -32601 },
                        { "message", "Methode non prise en charge par Kivrio Agent UI: " + method }
                    }
                }
            };
        }

        private static bool IsSafeCommandApproval(Dictionary<string, object> parameters, CodexTurnCapture capture)
        {
            if (capture == null) return false;
            if (capture.AllowExecutableSandbox && !IsPathUnderAnyRoot(capture.WorkspaceRoot, capture.WritableRoots))
            {
                return false;
            }

            string cwd = GetString(parameters, "cwd");
            if (string.IsNullOrWhiteSpace(cwd))
            {
                cwd = capture.WorkspaceRoot;
            }
            if (!IsPathUnderAnyRoot(cwd, capture.WritableRoots))
            {
                return false;
            }

            string command = GetString(parameters, "command");
            if (CommandContainsParentTraversal(command))
            {
                return false;
            }
            return CommandAbsolutePathsAreAllowed(command, capture.WritableRoots);
        }

        private static bool IsSafeFileChangeApproval(Dictionary<string, object> parameters, CodexTurnCapture capture)
        {
            if (capture == null) return false;
            string grantRoot = GetString(parameters, "grantRoot");
            if (string.IsNullOrWhiteSpace(grantRoot))
            {
                return IsPathUnderAnyRoot(capture.WorkspaceRoot, capture.WritableRoots);
            }
            return IsPathUnderAnyRoot(grantRoot, capture.WritableRoots);
        }

        private static Dictionary<string, object> BuildGrantedPermissions(Dictionary<string, object> parameters, CodexTurnCapture capture)
        {
            var granted = new Dictionary<string, object>();
            if (capture == null)
            {
                return granted;
            }

            Dictionary<string, object> requested = AsDictionary(GetObject(parameters, "permissions"));
            Dictionary<string, object> fileSystem = AsDictionary(GetObject(requested, "fileSystem"));
            var fileSystemGrant = new Dictionary<string, object>();

            object[] requestedWrites = FilterAllowedPaths(GetObject(fileSystem, "write"), capture.WritableRoots);
            if (requestedWrites.Length > 0)
            {
                fileSystemGrant["write"] = requestedWrites;
            }

            object[] requestedReads = FilterAllowedPaths(GetObject(fileSystem, "read"), capture.WritableRoots);
            if (requestedReads.Length > 0)
            {
                fileSystemGrant["read"] = requestedReads;
            }

            if (fileSystemGrant.Count > 0)
            {
                granted["fileSystem"] = fileSystemGrant;
            }
            return granted;
        }

        private static object[] FilterAllowedPaths(object value, object[] allowedRoots)
        {
            var granted = new List<object>();
            foreach (string path in StringListFromObject(value))
            {
                if (IsPathUnderAnyRoot(path, allowedRoots))
                {
                    AddUniqueRoot(granted, path);
                }
            }
            return granted.ToArray();
        }

        private static List<string> StringListFromObject(object value)
        {
            var values = new List<string>();
            if (value == null) return values;
            string text = value as string;
            if (text != null)
            {
                if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
                return values;
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null) return values;
            foreach (object item in enumerable)
            {
                string itemText = item == null ? "" : Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(itemText)) values.Add(itemText);
            }
            return values;
        }

        private static bool CommandAbsolutePathsAreAllowed(string command, object[] allowedRoots)
        {
            string value = command ?? "";
            foreach (Match match in Regex.Matches(value, "[\"']([A-Za-z]:\\\\[^\"']+)[\"']"))
            {
                if (!IsPathUnderAnyRoot(match.Groups[1].Value, allowedRoots))
                {
                    return false;
                }
            }
            foreach (Match match in Regex.Matches(value, "\\b[A-Za-z]:\\\\[^\\s\"']+"))
            {
                if (!IsPathUnderAnyRoot(match.Value, allowedRoots))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool CommandContainsParentTraversal(string command)
        {
            string value = command ?? "";
            return Regex.IsMatch(value, @"(^|[\s""'])\.\.(\\|/)");
        }

        private static bool IsPathUnderAnyRoot(string path, object[] allowedRoots)
        {
            string normalizedPath = NormalizeFullPath(path);
            if (string.IsNullOrEmpty(normalizedPath)) return false;
            foreach (object root in allowedRoots ?? new object[0])
            {
                string normalizedRoot = NormalizeFullPath(Convert.ToString(root));
                if (string.IsNullOrEmpty(normalizedRoot)) continue;
                if (SamePath(normalizedPath, normalizedRoot)) return true;
                string rootWithSlash = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NormalizeFullPath(string path)
        {
            string value = (path ?? "").Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return "";
            }
        }

        private static bool SamePath(string left, string right)
        {
            string a = NormalizeFullPath(left);
            string b = NormalizeFullPath(right);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(
                a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void CaptureCompletedItem(CodexTurnCapture capture, Dictionary<string, object> parameters)
        {
            Dictionary<string, object> item = AsDictionary(GetObject(parameters, "item"));
            string type = GetString(item, "type");
            if (type == "agentMessage")
            {
                string text = GetString(item, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    capture.CompletedAgentMessage = text;
                }
            }
            else if (type == "reasoning")
            {
                string text = JoinStringValues(GetObject(item, "content"));
                if (string.IsNullOrEmpty(text))
                {
                    text = JoinStringValues(GetObject(item, "summary"));
                }
                if (!string.IsNullOrEmpty(text))
                {
                    capture.CompletedReasoning = text;
                }
            }
            else if (type == "commandExecution")
            {
                string status = GetString(item, "status");
                string command = GetString(item, "command");
                string output = GetString(item, "aggregatedOutput");
                object exitValue = GetObject(item, "exitCode");
                string exitText = exitValue == null ? "" : Convert.ToString(exitValue);
                bool failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(exitText) && exitText != "0");
                if (failed)
                {
                    if (capture.ToolNotes.Length > 0) capture.ToolNotes.AppendLine();
                    capture.ToolNotes.Append("Commande Codex echouee");
                    if (!string.IsNullOrEmpty(exitText)) capture.ToolNotes.Append(" (exit ").Append(exitText).Append(")");
                    if (!string.IsNullOrEmpty(command)) capture.ToolNotes.Append(": ").Append(command);
                    if (!string.IsNullOrEmpty(output)) capture.ToolNotes.AppendLine().Append(output.Trim());
                }
            }
            else if (type == "fileChange")
            {
                string status = GetString(item, "status");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    if (capture.ToolNotes.Length > 0) capture.ToolNotes.AppendLine();
                    capture.ToolNotes.Append("Modification de fichier Codex echouee.");
                }
            }
        }

        private static string ExtractTurnError(Dictionary<string, object> parameters)
        {
            Dictionary<string, object> turn = AsDictionary(GetObject(parameters, "turn"));
            string status = GetString(turn, "status");
            Dictionary<string, object> error = AsDictionary(GetObject(turn, "error"));
            string message = GetString(error, "message");
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }
            string code = GetString(error, "code");
            if (!string.IsNullOrEmpty(code))
            {
                return code;
            }
            if (status == "failed")
            {
                return "Le tour Codex CLI a echoue.";
            }
            return "";
        }

        private void SendJson(ClientWebSocket socket, Dictionary<string, object> payload, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();
        }

        private Dictionary<string, object> ReceiveJson(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using (var output = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("Connexion Codex CLI fermee.");
                    }
                    output.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string text = Encoding.UTF8.GetString(output.ToArray());
                object parsed = _json.DeserializeObject(text);
                return AsDictionary(parsed);
            }
        }

        private static bool SameId(object value, int expected)
        {
            if (value is int) return (int)value == expected;
            if (value is long) return (long)value == expected;
            string text = Convert.ToString(value);
            int number;
            return int.TryParse(text, out number) && number == expected;
        }

        private static string JsonRpcErrorMessage(object error)
        {
            Dictionary<string, object> dict = AsDictionary(error);
            string message = GetString(dict, "message");
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }
            return "Erreur JSON-RPC Codex CLI.";
        }

        private static object GetObject(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object value = GetObject(dict, key);
            return value == null ? "" : Convert.ToString(value);
        }

        private static string GetNestedString(Dictionary<string, object> dict, string first, string second)
        {
            return GetString(AsDictionary(GetObject(dict, first)), second);
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            var dict = value as Dictionary<string, object>;
            return dict ?? new Dictionary<string, object>();
        }

        private static string JoinStringValues(object value)
        {
            if (value == null)
            {
                return "";
            }
            string text = value as string;
            if (text != null)
            {
                return text;
            }
            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                return Convert.ToString(value);
            }

            var builder = new StringBuilder();
            foreach (object item in enumerable)
            {
                if (item == null) continue;
                if (builder.Length > 0) builder.Append("\n");
                builder.Append(Convert.ToString(item));
            }
            return builder.ToString();
        }
    }

    internal sealed class CodexTurnCapture
    {
        public readonly string ThreadId;
        public readonly string WorkspaceRoot;
        public readonly object[] WritableRoots;
        public readonly bool AllowExecutableSandbox;
        public readonly StringBuilder Answer = new StringBuilder();
        public readonly StringBuilder Reasoning = new StringBuilder();
        public readonly StringBuilder ToolNotes = new StringBuilder();
        public string TurnId;
        public bool Completed;
        public string Error;
        public string CompletedAgentMessage;
        public string CompletedReasoning;

        public CodexTurnCapture(string threadId, string workspaceRoot, object[] writableRoots, bool allowExecutableSandbox)
        {
            ThreadId = threadId;
            WorkspaceRoot = workspaceRoot;
            WritableRoots = writableRoots ?? new object[0];
            AllowExecutableSandbox = allowExecutableSandbox;
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
            if (status == HttpStatusCode.BadGateway) return "Bad Gateway";
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
                    agent = CodingAgentCatalog.NormalizeId(GetString(body, "agent", GetString(body, "agentId", CodingAgentCatalog.DefaultId))),
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
                if (body.ContainsKey("agent") || body.ContainsKey("agentId"))
                {
                    string nextAgent = CodingAgentCatalog.NormalizeId(GetString(body, "agent", GetString(body, "agentId", conversation.agent)));
                    if (conversation.messages == null || conversation.messages.Count == 0 || string.IsNullOrEmpty(conversation.agent))
                    {
                        conversation.agent = nextAgent;
                    }
                }
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
                conversation.agent = CodingAgentCatalog.NormalizeId(conversation.agent);
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
                { "agent", CodingAgentCatalog.NormalizeId(conversation.agent) },
                { "agentLabel", CodingAgentCatalog.FromValue(conversation.agent).Label },
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
        public string agent { get; set; }
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
