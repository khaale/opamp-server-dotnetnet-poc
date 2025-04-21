using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using Opamp.Proto; // Namespace from generated code
using OpampServerDemo.Services; // Namespace for ConfigurationService

namespace OpampServerDemo.Handlers;

public class OpampHandler
{
    private readonly ILogger<OpampHandler> _logger;
    private readonly IConfigurationService _configService;

    // Store active connections (simple in-memory)
    // Key: instance_uid as ByteString (can be complex), Value: WebSocket
    // Consider a more robust AgentState class for real applications
    private static readonly ConcurrentDictionary<ByteString, WebSocket> _connections = new();

    public OpampHandler(ILogger<OpampHandler> logger, IConfigurationService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    public async Task HandleWebSocketAsync(WebSocket webSocket)
    {
        ByteString? agentInstanceUid = null; // Track the UID for this connection
        _logger.LogInformation("WebSocket connection established.");

        var buffer = new byte[1024 * 4]; // Buffer for receiving messages
        var messageStream = new MemoryStream(); // To accumulate message chunks

        try
        {
            WebSocketReceiveResult result;
            do
            {
                // Read chunks until end of message
                messageStream.Seek(0, SeekOrigin.Begin); // Reset stream for potential re-use if needed later
                messageStream.SetLength(0); // Clear previous message data

                do
                {
                    // Rent buffer if message is larger than initial buffer
                    var bufferSegment = new ArraySegment<byte>(buffer);
                    result = await webSocket.ReceiveAsync(bufferSegment, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close message received from Agent {AgentId}", agentInstanceUid?.ToBase64() ?? "<unknown>");
                        break; // Exit inner loop
                    }
                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        _logger.LogWarning("Received non-binary message type: {MessageType}. Ignoring.", result.MessageType);
                        // Optionally close the connection here for protocol violation
                        continue; // Or break
                    }

                    await messageStream.WriteAsync(bufferSegment.Array!, bufferSegment.Offset, result.Count, CancellationToken.None);

                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break; // Exit outer loop
                }

                // --- Process the complete message ---
                messageStream.Seek(0, SeekOrigin.Begin); // Rewind stream to read the full message
                if (messageStream.Length == 0) continue; // Nothing received

                try
                {
                    var agentRequest = AgentToServer.Parser.ParseFrom(messageStream);

                    if (agentRequest.InstanceUid == null || agentRequest.InstanceUid.IsEmpty)
                    {
                        // This might be an initial connection asking for a UID, or an error
                        // For this demo, we'll ignore messages without UID for simplicity,
                        // assuming the agent provides one. A real server might generate one.
                        _logger.LogWarning("Received message without InstanceUid. Ignoring.");
                        continue;
                    }

                    // Update agent tracking
                    agentInstanceUid = agentRequest.InstanceUid;
                    _connections.AddOrUpdate(agentInstanceUid, webSocket, (key, oldSocket) => webSocket); // Store/update socket

                    _logger.LogInformation("Received AgentToServer from {AgentId}, SeqNum: {SeqNum}",
                        agentInstanceUid.ToBase64(), agentRequest.SequenceNum);

                    if (agentRequest.AgentDescription != null)
                    {
                         _logger.LogDebug("Agent Description: {Description}", agentRequest.AgentDescription);
                    }

                    // Prepare the response
                    var serverResponse = await PrepareServerToAgentResponse(agentRequest);

                    // Send the response
                    byte[] responseBytes = serverResponse.ToByteArray();
                    await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                    _logger.LogInformation("Sent ServerToAgent to {AgentId}", agentInstanceUid.ToBase64());

                }
                catch (InvalidProtocolBufferException ex)
                {
                    _logger.LogError(ex, "Failed to parse AgentToServer protobuf message.");
                    // Consider sending a ServerErrorResponse or closing connection
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing AgentToServer message from {AgentId}", agentInstanceUid?.ToBase64() ?? "<unknown>");
                    // Gracefully handle unexpected errors
                }

            } while (!result.CloseStatus.HasValue);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
             _logger.LogWarning("WebSocket connection closed prematurely for Agent {AgentId}", agentInstanceUid?.ToBase64() ?? "<unknown>");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket error for Agent {AgentId}", agentInstanceUid?.ToBase64() ?? "<unknown>");
        }
        finally
        {
            // Clean up connection state
            if (agentInstanceUid != null)
            {
                _connections.TryRemove(agentInstanceUid, out _);
                 _logger.LogInformation("Cleaned up connection state for Agent {AgentId}", agentInstanceUid.ToBase64());
            }

            // Ensure socket is closed if still open
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
            }
             _logger.LogInformation("WebSocket connection closed for Agent {AgentId}", agentInstanceUid?.ToBase64() ?? "<unknown>");
            webSocket.Dispose();
        }
    }

    private async Task<ServerToAgent> PrepareServerToAgentResponse(AgentToServer agentRequest)
    {
        var serverResponse = new ServerToAgent
        {
            InstanceUid = agentRequest.InstanceUid,
            // --- Server Capabilities ---
            // Tell the agent we support remote config. Add other capabilities if implemented.
            Capabilities = (ulong)ServerCapabilities.OffersRemoteConfig
                          | (ulong)ServerCapabilities.AcceptsStatus // Should always accept status
                          // Add more caps here as needed, e.g.:
                          // | (ulong)ServerCapabilities.AcceptsEffectiveConfig
        };

        // --- Configuration Check ---
        AgentRemoteConfig desiredConfig = _configService.GetCurrentConfig();
        ByteString? agentReportedHash = agentRequest.RemoteConfigStatus?.LastRemoteConfigHash;

        bool needsConfigUpdate = false;
        if (agentReportedHash == null || agentReportedHash.IsEmpty)
        {
            // Agent didn't report a hash (initial connection?) -> Send config
            needsConfigUpdate = true;
            _logger.LogInformation("Agent {AgentId} did not report a config hash. Sending current config.", agentRequest.InstanceUid.ToBase64());
        }
        else if (!agentReportedHash.Equals(desiredConfig.ConfigHash))
        {
            // Hashes differ -> Send config
            needsConfigUpdate = true;
            _logger.LogInformation("Agent {AgentId} hash ({AgentHash}) differs from server hash ({ServerHash}). Sending updated config.",
                agentRequest.InstanceUid.ToBase64(),
                agentReportedHash.ToBase64(),
                desiredConfig.ConfigHash.ToBase64());
        }
        else
        {
             _logger.LogDebug("Agent {AgentId} hash matches server hash. No config update needed.", agentRequest.InstanceUid.ToBase64());
        }


        if (needsConfigUpdate)
        {
            serverResponse.RemoteConfig = desiredConfig; // Attach the full config object
        }

        // --- Handle other AgentToServer fields (Optional for this demo) ---
        if (agentRequest.RemoteConfigStatus != null)
        {
            _logger.LogDebug("Agent {AgentId} RemoteConfigStatus: {Status}, Hash: {Hash}, Error: {Error}",
                agentRequest.InstanceUid.ToBase64(),
                agentRequest.RemoteConfigStatus.Status,
                agentRequest.RemoteConfigStatus.LastRemoteConfigHash?.ToBase64() ?? "N/A",
                agentRequest.RemoteConfigStatus.ErrorMessage ?? "N/A");
            // You could add logic here based on status (e.g., log failures)
        }

        // --- Add other ServerToAgent fields if needed ---
        // Example: If agent requests UID generation
        // if ((agentRequest.Flags & (ulong)AgentToServerFlags.RequestInstanceUid) != 0) {
        //    // Generate and assign UID in AgentIdentification
        //    serverResponse.AgentIdentification = new AgentIdentification { NewInstanceUid = ... };
        // }

        // Example: Acknowledge health report if agent sends one
        // if (agentRequest.Health != null) { ... }

        return serverResponse; // Return await Task.FromResult(serverResponse); if not async needed inside
    }
}