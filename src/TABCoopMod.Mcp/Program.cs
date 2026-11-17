using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace TABCoopMod.Mcp
{
    /// <summary>
    /// Minimal MCP (Model Context Protocol) stdio server that proxies tool calls to the in-process
    /// TABCoopMod control server. Each MCP tool maps 1:1 to a control-server tool, so an agent can
    /// drive the running game (move/build/train/cancel/pause) and receive auto-captured screenshots.
    ///
    /// stdout carries ONLY newline-delimited JSON-RPC messages; diagnostics go to stderr.
    /// </summary>
    internal static class Program
    {
        private const int DefaultPort = 27901;
        private static readonly JsonArray Tools = BuildTools();
        private static ControlClient _client;
        private static long _requestId;

        private static int Main(string[] args)
        {
            try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            int port = ResolvePort(args);
            _client = new ControlClient(port);
            Console.Error.WriteLine($"[tabcoop-mcp] proxying to game control server on 127.0.0.1:{port}");

            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode root;
                try { root = JsonNode.Parse(line); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tabcoop-mcp] bad json: {ex.Message}");
                    continue;
                }

                try { Handle(root); }
                catch (Exception ex) { Console.Error.WriteLine($"[tabcoop-mcp] handler error: {ex}"); }
            }
            return 0;
        }

        private static int ResolvePort(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) return p;
            }
            string env = Environment.GetEnvironmentVariable("TABCOOP_CONTROL_PORT");
            return int.TryParse(env, out int ep) ? ep : DefaultPort;
        }

        private static void Handle(JsonNode root)
        {
            string method = root?["method"]?.GetValue<string>();
            JsonNode id = root?["id"];
            if (string.IsNullOrEmpty(method)) return;

            switch (method)
            {
                case "initialize":
                    WriteResult(id, BuildInitializeResult(root));
                    break;

                case "notifications/initialized":
                case "notifications/cancelled":
                    break;

                case "ping":
                    WriteResult(id, new JsonObject());
                    break;

                case "tools/list":
                    WriteResult(id, new JsonObject { ["tools"] = Tools.DeepClone() });
                    break;

                case "tools/call":
                    WriteResult(id, CallTool(root));
                    break;

                case "resources/list":
                    WriteResult(id, new JsonObject { ["resources"] = new JsonArray() });
                    break;

                case "prompts/list":
                    WriteResult(id, new JsonObject { ["prompts"] = new JsonArray() });
                    break;

                default:
                    if (id != null) WriteError(id, -32601, $"method not found: {method}");
                    break;
            }
        }

        private static JsonObject BuildInitializeResult(JsonNode root)
        {
            string requested = root?["params"]?["protocolVersion"]?.GetValue<string>();
            return new JsonObject
            {
                ["protocolVersion"] = string.IsNullOrEmpty(requested) ? "2024-11-05" : requested,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "tabcoop-game", ["version"] = "0.1.0" }
            };
        }

        private static JsonObject CallTool(JsonNode root)
        {
            JsonNode p = root?["params"];
            string name = p?["name"]?.GetValue<string>();
            JsonNode args = p?["arguments"] ?? new JsonObject();
            if (string.IsNullOrEmpty(name)) return ErrorContent("missing tool name");

            string raw;
            try { raw = _client.Send(++_requestId, name, args); }
            catch (Exception ex)
            {
                return ErrorContent($"control server unavailable ({ex.Message}). " +
                                    "Is the game running with TABCOOP_CONTROL_PORT set?");
            }

            JsonNode resp;
            try { resp = JsonNode.Parse(raw); }
            catch { return ErrorContent("invalid response from control server"); }

            bool ok = resp?["ok"]?.GetValue<bool>() ?? false;
            if (!ok) return ErrorContent(resp?["error"]?.GetValue<string>() ?? "tool failed");

            JsonNode result = resp["result"];
            WaitForScreenshot(result);

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result != null ? result.ToJsonString() : "{}"
                }),
                ["isError"] = false
            };
        }

        private static void WaitForScreenshot(JsonNode result)
        {
            string path = result?["screenshot"]?.GetValue<string>();
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < 40 && !File.Exists(path); i++)
                Thread.Sleep(50);
        }

        private static JsonObject ErrorContent(string message)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
                ["isError"] = true
            };
        }

        private static void WriteResult(JsonNode id, JsonNode result)
        {
            if (id == null) return; // notification: no response
            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
            Console.Out.WriteLine(resp.ToJsonString());
            Console.Out.Flush();
        }

        private static void WriteError(JsonNode id, int code, string message)
        {
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            };
            Console.Out.WriteLine(resp.ToJsonString());
            Console.Out.Flush();
        }

        // ====================================================================
        // Tool schemas
        // ====================================================================

        private static JsonObject Str(string desc) => new JsonObject { ["type"] = "string", ["description"] = desc };
        private static JsonObject Num(string desc) => new JsonObject { ["type"] = "number", ["description"] = desc };
        private static JsonObject Int(string desc) => new JsonObject { ["type"] = "integer", ["description"] = desc };
        private static JsonObject Bool(string desc) => new JsonObject { ["type"] = "boolean", ["description"] = desc };

        private static JsonObject IdArray(string desc) => new JsonObject
        {
            ["type"] = "array",
            ["description"] = desc,
            ["items"] = new JsonObject { ["type"] = new JsonArray("string", "number") }
        };

        private static JsonObject Tool(string name, string desc, JsonObject props, JsonArray required)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props ?? new JsonObject(),
                    ["required"] = required ?? new JsonArray()
                }
            };
        }

        private static JsonObject WithShot(JsonObject props)
        {
            props["screenshot"] = Bool("Capture a PNG after this operation (default true).");
            return props;
        }

        private static JsonArray Req(params string[] names)
        {
            var arr = new JsonArray();
            foreach (string n in names) arr.Add(JsonValue.Create(n));
            return arr;
        }

        private static JsonArray BuildTools()
        {
            var tools = new JsonArray();

            tools.Add(Tool("game_state",
                "Current simulation tick, pause state, resources and entity counts.",
                new JsonObject(), new JsonArray()));

            tools.Add(Tool("list_entities",
                "List live entities (buildings/units/zombies) with id, type, position, hp, team. IDs are strings (64-bit).",
                new JsonObject
                {
                    ["typeContains"] = Str("Case-insensitive substring of the entity type name (e.g. Ranger, CommandCenter, Zombie)."),
                    ["onlyPlayer"] = Bool("Only player-team entities."),
                    ["movable"] = Bool("Only entities that have a movable component."),
                    ["includeProjectiles"] = Bool("Include projectiles (default false)."),
                    ["limit"] = Int("Max rows (default 50, cap 5000).")
                }, new JsonArray()));

            tools.Add(Tool("move_units",
                "Issue a group move for the given unit IDs to a world position.",
                WithShot(new JsonObject
                {
                    ["ids"] = IdArray("Unit entity IDs to move."),
                    ["x"] = Num("Target world X."),
                    ["y"] = Num("Target world Y."),
                    ["shift"] = Bool("Queue the move (shift).")
                }), Req("ids", "x", "y")));

            tools.Add(Tool("place_building",
                "Place a building at a world position (deducts resources through the normal Build command).",
                WithShot(new JsonObject
                {
                    ["buildingType"] = Str("Full type name, e.g. ZX.Entities.TentHouse."),
                    ["x"] = Num("Target world X."),
                    ["y"] = Num("Target world Y."),
                    ["sizeW"] = Num("Footprint width in cells (default 1)."),
                    ["sizeH"] = Num("Footprint height in cells (default 1)."),
                    ["builderId"] = Str("Optional builder entity ID (defaults to the Command Center)."),
                    ["isBonus"] = Bool("Bonus building flag."),
                    ["instant"] = Bool("Instant build flag."),
                    ["shift"] = Bool("Queue the build (shift).")
                }), Req("x", "y")));

            tools.Add(Tool("train_unit",
                "Train a unit from a barracks/production building.",
                WithShot(new JsonObject
                {
                    ["barracksId"] = Str("Production building entity ID."),
                    ["unitType"] = Str("Full unit type name, e.g. ZX.Entities.Ranger.")
                }), Req("barracksId")));

            tools.Add(Tool("cancel_current_order",
                "Cancel the current order/construction of the given entities.",
                WithShot(new JsonObject { ["ids"] = IdArray("Entity IDs.") }), Req("ids")));

            tools.Add(Tool("cancel_queued_order",
                "Cancel a queued production slot of the given entities.",
                WithShot(new JsonObject
                {
                    ["ids"] = IdArray("Entity IDs."),
                    ["slot"] = Int("Queue slot index (0-based).")
                }), Req("ids")));

            tools.Add(Tool("set_paused",
                "Pause or resume the simulation.",
                WithShot(new JsonObject { ["paused"] = Bool("True to pause, false to resume.") }), Req("paused")));

            tools.Add(Tool("set_game_speed",
                "Set the simulation speed multiplier (0 = pause, 1 = normal, up to 20).",
                WithShot(new JsonObject { ["speed"] = Num("Speed multiplier.") }), Req("speed")));

            tools.Add(Tool("request_pause",
                "Local pause intent: pausing is immediate; resuming runs a short countdown (default 3s) before resuming.",
                WithShot(new JsonObject { ["paused"] = Bool("true = pause now, false = resume after countdown.") }), Req("paused")));

            tools.Add(Tool("list_mayors",
                "List mayors (id/name/level) from the registry.",
                new JsonObject { ["level"] = Int("Filter by level (0 = all).") }, new JsonArray()));

            tools.Add(Tool("mayor_select",
                "Select/apply a mayor by its ID (deterministic — sent by ID, no per-peer re-roll).",
                WithShot(new JsonObject { ["mayorId"] = Str("Mayor ID from list_mayors.") }), Req("mayorId")));

            tools.Add(Tool("issue_command",
                "Escape hatch: dispatch a raw NetCommand (message-shape from docs/04_NETWORK_PROTOCOL.md).",
                WithShot(new JsonObject
                {
                    ["type"] = Int("NetCommandType (1=ExecuteCommand, 2=GroupMoveOrPatrol, 3=PlaceBuilding, 4=CancelCurrent, 5=CancelQueue, 6=SetPaused)."),
                    ["playerId"] = Int("Player id (default 1)."),
                    ["commandId"] = Int("ZXCommand ID (e.g. returned by a prior query)."),
                    ["buildingType"] = Str("Building type name for PlaceBuilding."),
                    ["ids"] = IdArray("Executor entity IDs."),
                    ["targetEntityId"] = Str("Target entity ID (attack/repair)."),
                    ["x"] = Num("Target world X."),
                    ["y"] = Num("Target world Y."),
                    ["sizeW"] = Num("Target size width."),
                    ["sizeH"] = Num("Target size height."),
                    ["originX"] = Num("Target origin X."),
                    ["originY"] = Num("Target origin Y."),
                    ["shift"] = Bool("Shift-queued."),
                    ["slot"] = Int("Queue slot."),
                    ["paused"] = Bool("Paused state for SetPaused.")
                }), new JsonArray()));

            tools.Add(Tool("screenshot",
                "Capture the current frame to a PNG and return its path (for human/vision review).",
                new JsonObject { ["tag"] = Str("Optional label used in the file name.") }, new JsonArray()));

            tools.Add(Tool("get_camera",
                "Current camera world rectangle and screen size, for world→pixel mapping.",
                new JsonObject(), new JsonArray()));

            tools.Add(Tool("set_camera",
                "Move and/or zoom the camera to a world position (or entity), then auto-capture a screenshot there.",
                WithShot(new JsonObject
                {
                    ["x"] = Num("Target center world X."),
                    ["y"] = Num("Target center world Y."),
                    ["w"] = Num("Camera world width (optional)."),
                    ["h"] = Num("Camera world height (optional)."),
                    ["zoom"] = Num("Zoom factor (2 = 2x closer)."),
                    ["entityId"] = Str("Center on this entity instead of x/y.")
                }), new JsonArray()));

            tools.Add(Tool("reveal_map",
                "Reveal the whole map (ShowFullMap=true, fog disabled). Rendering-only; does not change the simulation.",
                WithShot(new JsonObject
                {
                    ["enabled"] = Bool("true = reveal, false = restore fog.")
                }), new JsonArray()));

            return tools;
        }
    }
}
