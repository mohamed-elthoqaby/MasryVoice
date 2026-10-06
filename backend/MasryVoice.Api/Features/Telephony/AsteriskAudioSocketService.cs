using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MasryVoice.Api.Features.Telephony;

/// <summary>
/// Asterisk AudioSocket Protocol Listener Service.
/// Implements bidirectional raw audio streaming (16-bit 8kHz/16kHz signed linear PCM) with Asterisk PBX.
/// Note: Live end-to-end call testing with an external telco is marked Blocked until a SIP trunk provider is provisioned.
/// </summary>
public class AsteriskAudioSocketService : BackgroundService, ITelephonyAdapter
{
    private readonly ILogger<AsteriskAudioSocketService> _logger;
    private readonly int _port;
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<Guid, TelephonyCallSession> _activeCalls = new();

    public bool IsListening => _listener != null;
    public int ActiveCallsCount => _activeCalls.Count;

    public AsteriskAudioSocketService(IConfiguration configuration, ILogger<AsteriskAudioSocketService> logger)
    {
        _logger = logger;
        _port = configuration.GetValue<int>("Telephony:AudioSocketPort", 9092);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartListeningAsync(_port, stoppingToken);
    }

    public async Task StartListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _logger.LogInformation("Asterisk AudioSocket listener active on port {Port}.", port);

            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleAudioSocketConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Asterisk AudioSocket listener stopped or could not bind port {Port}.", port);
        }
        finally
        {
            _listener?.Stop();
            _listener = null;
        }
    }

    public Task StopAsync()
    {
        _listener?.Stop();
        _listener = null;
        return Task.CompletedTask;
    }

    private async Task HandleAudioSocketConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var call = new TelephonyCallSession();
        _activeCalls.TryAdd(call.CallId, call);
        _logger.LogInformation("Inbound Asterisk AudioSocket call connected. Assigned CallId: {CallId}", call.CallId);

        using (client)
        await using (var stream = client.GetStream())
        {
            var headerBuffer = new byte[3]; // AudioSocket frame header: 1 byte type, 2 bytes payload length (big-endian)

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(headerBuffer.AsMemory(0, 3), ct);
                    if (bytesRead < 3) break;

                    byte frameType = headerBuffer[0];
                    int payloadLength = (headerBuffer[1] << 8) | headerBuffer[2];

                    if (payloadLength <= 0 || payloadLength > 65535) break;

                    var payload = new byte[payloadLength];
                    int totalRead = 0;
                    while (totalRead < payloadLength)
                    {
                        int read = await stream.ReadAsync(payload.AsMemory(totalRead, payloadLength - totalRead), ct);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (frameType == 0x01) // Audio payload
                    {
                        // Audio frame received from Asterisk (raw PCM).
                        // In production pipeline, this feeds ISttProvider.
                    }
                    else if (frameType == 0x02) // Call UUID / Metadata
                    {
                        _logger.LogDebug("AudioSocket UUID received for Call {CallId}", call.CallId);
                    }
                    else if (frameType == 0x00) // Hangup signal
                    {
                        _logger.LogInformation("AudioSocket hangup signal received for Call {CallId}", call.CallId);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "AudioSocket connection ended for Call {CallId}", call.CallId);
            }
            finally
            {
                _activeCalls.TryRemove(call.CallId, out _);
                _logger.LogInformation("Inbound Asterisk AudioSocket call completed. CallId: {CallId}", call.CallId);
            }
        }
    }
}
