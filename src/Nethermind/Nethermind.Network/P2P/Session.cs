// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class Session : ISession
    {
        private static readonly ConcurrentDictionary<string, AdaptiveCodeResolver> _resolvers = new();
        private readonly ConcurrentDictionary<string, IProtocolHandler> _protocols = new();

        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private Node? _node;
        private readonly IChannel _channel;
        private readonly IDisconnectsAnalyzer _disconnectsAnalyzer;
        private IChannelHandlerContext? _context;

        public Session(
            int localPort,
            IChannel channel,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            ILogManager logManager)
        {
            Direction = ConnectionDirection.In;
            State = SessionState.New;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _disconnectsAnalyzer = disconnectsAnalyzer;
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = null;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
        }

        public Session(
            int localPort,
            Node remoteNode,
            IChannel channel,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            ILogManager logManager)
        {
            State = SessionState.New;
            _node = remoteNode ?? throw new ArgumentNullException(nameof(remoteNode));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _disconnectsAnalyzer = disconnectsAnalyzer;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = remoteNode.Id;
            RemoteHost = remoteNode.Host;
            RemotePort = remoteNode.Port;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
            Direction = ConnectionDirection.Out;
        }

        public bool IsClosing => State > SessionState.Initialized;
        private bool IsClosed => State > SessionState.DisconnectingProtocols;
        public bool IsNetworkIdMatched { get; set; }
        public int LocalPort { get; set; }
        public PublicKey? RemoteNodeId { get; set; }
        public PublicKey ObsoleteRemoteNodeId { get; set; }
        public string RemoteHost { get; set; }
        public int RemotePort { get; set; }
        public DateTime LastPingUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
        public ConnectionDirection Direction { get; }
        public Guid SessionId { get; }

        public Node Node
        {
            get
            {
                //It is needed for lazy creation of Node, in case  IN connections, publicKey is available only after handshake
                if (_node is null)
                {
                    if (RemoteNodeId is null || RemoteHost is null || RemotePort == 0)
                    {
                        throw new InvalidOperationException("Cannot create a session's node object without knowing remote node details");
                    }

                    _node = new Node(RemoteNodeId, RemoteHost, RemotePort);
                }

                return _node;
            }

            private set => _node = value;
        }

        public void EnableSnappy()
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(EnableSnappy)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Enabling Snappy compression and disabling framing in {this}");
            _context.Channel.Pipeline.Get<ZeroPacketSplitter>()?.DisableFraming();

            // since groups were used, we are on a different thread
            _context.Channel.Pipeline.Get<ZeroNettyP2PHandler>()?.EnableSnappy();
            // code in the next line does no longer work as if there is a packet waiting then it will skip the snappy decoder
            // _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new ZeroSnappyEncoder(_logManager));
        }

        public void AddSupportedCapability(Capability capability)
        {
            if (!_protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol))
            {
                return;
            }
            if (protocol is IP2PProtocolHandler p2PProtocol)
            {
                p2PProtocol.AddSupportedCapability(capability);
            }
        }

        public bool HasAvailableCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol)
               && protocol is IP2PProtocolHandler p2PProtocol
               && p2PProtocol.HasAvailableCapability(capability);

        public bool HasAgreedCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol)
               && protocol is IP2PProtocolHandler p2PProtocol
               && p2PProtocol.HasAgreedCapability(capability);

        public IPingSender PingSender { get; set; }

        private (DisconnectReason, string?)? _disconnectAfterInitialized = null;

        public void ReceiveMessage(ZeroPacket zeroPacket)
        {
            Interlocked.Add(ref Metrics.P2PBytesReceived, zeroPacket.Content.ReadableBytes);

            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(ReceiveMessage)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = zeroPacket.PacketType;
            (string? protocol, int messageId) = _resolver.ResolveProtocol(zeroPacket.PacketType);
            zeroPacket.Protocol = protocol;

            MsgReceived?.Invoke(this, new PeerEventArgs(_node, zeroPacket.Protocol, zeroPacket.PacketType, zeroPacket.Content.ReadableBytes));

            RecordIncomingMessageMetric(zeroPacket.Protocol, messageId, zeroPacket.Content.ReadableBytes);

            if (_logger.IsTrace)
                _logger.Trace($"{this} received a message of length {zeroPacket.Content.ReadableBytes} " +
                              $"({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol is null)
            {
                if (_logger.IsTrace)
                    _logger.Warn($"Received a message from node: {RemoteNodeId}, " +
                                 $"({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): " +
                                 $"{string.Join(", ", _protocols.Select(static x => $"{x.Value.Name} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            zeroPacket.PacketType = (byte)messageId;
            IProtocolHandler protocolHandler = _protocols[protocol];
            if (protocolHandler is IZeroProtocolHandler zeroProtocolHandler)
            {
                zeroProtocolHandler.HandleMessage(zeroPacket);
            }
            else
            {
                protocolHandler.HandleMessage(new Packet(zeroPacket));
            }
        }

        public int DeliverMessage<T>(T message) where T : P2PMessage
        {
            try
            {
                lock (_sessionStateLock)
                {
                    if (State < SessionState.Initialized)
                    {
                        throw new InvalidOperationException($"{nameof(DeliverMessage)} called {this}");
                    }

                    // Must allow sending out packet when `DisconnectingProtocols` so that we can send out disconnect reason
                    // and hello (part of protocol)
                    if (IsClosed)
                    {
                        return 1;
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"P2P to deliver {message.Protocol}.{message.PacketType} on {this}");

                message.AdaptivePacketType = _resolver.ResolveAdaptiveId(message.Protocol, message.PacketType);
                int size = _packetSender.Enqueue(message);

                MsgDelivered?.Invoke(this, new PeerEventArgs(_node, message.Protocol, message.PacketType, size));

                RecordOutgoingMessageMetric(message, size);

                Interlocked.Add(ref Metrics.P2PBytesSent, size);

                return size;
            }
            finally
            {
                message.Dispose();
            }
        }

        public void ReceiveMessage(Packet packet)
        {
            Interlocked.Add(ref Metrics.P2PBytesReceived, packet.Data.Length);

            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(ReceiveMessage)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = _resolver.ResolveProtocol(packet.PacketType);
            packet.Protocol = protocol;

            MsgReceived?.Invoke(this, new PeerEventArgs(_node, packet.Protocol, packet.PacketType, packet.Data.Length));

            RecordIncomingMessageMetric(protocol, messageId, packet.Data.Length);

            if (_logger.IsTrace)
                _logger.Trace($"{this} received a message of length {packet.Data.Length} " +
                              $"({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol is null)
            {
                if (_logger.IsTrace)
                    _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), " +
                                 $"known protocols ({_protocols.Count}): " +
                                 $"{string.Join(", ", _protocols.Select(static x => $"{x.Value.Name} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            packet.PacketType = messageId;

            if (State < SessionState.DisconnectingProtocols)
            {
                _protocols[protocol].HandleMessage(packet);
            }
        }

        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler)
        {
            return _protocols.TryGetValue(protocolCode, out handler);
        }

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            if (_logger.IsTrace) _logger.Trace($"{nameof(Init)} called on {this}");

            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(packetSender);

            P2PVersion = p2PVersion;
            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (State != SessionState.HandshakeComplete)
                {
                    throw new InvalidOperationException($"{nameof(Init)} called on {this}");
                }

                _context = context;
                _packetSender = packetSender;
                State = SessionState.Initialized;
            }

            Initialized?.Invoke(this, EventArgs.Empty);

            // Disconnect may send disconnect reason message. But the hello message must be sent first, which is done
            // during Initialized event.
            // https://github.com/ethereum/devp2p/blob/master/rlpx.md#user-content-hello-0x00
            if (_disconnectAfterInitialized is not null)
            {
                InitiateDisconnect(_disconnectAfterInitialized.Value.Item1, _disconnectAfterInitialized.Value.Item2);
                _disconnectAfterInitialized = null;
            }
        }

        public void Handshake(PublicKey? handshakeRemoteNodeId)
        {
            if (_logger.IsTrace) _logger.Trace($"{nameof(Handshake)} called on {this}");
            lock (_sessionStateLock)
            {
                if (State == SessionState.Initialized || State == SessionState.HandshakeComplete)
                {
                    throw new InvalidOperationException($"{nameof(Handshake)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }

                State = SessionState.HandshakeComplete;
            }

            //For IN connections we don't have NodeId until this moment, so we need to set it in Session
            //For OUT connections it is possible remote id is different than what we had persisted or received from Discovery
            //If that is the case we need to set it in the session
            if (RemoteNodeId is null)
            {
                RemoteNodeId = handshakeRemoteNodeId;
            }
            else if (handshakeRemoteNodeId is not null && RemoteNodeId != handshakeRemoteNodeId)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Different NodeId received in handshake: old: {RemoteNodeId}, new: {handshakeRemoteNodeId}");
                ObsoleteRemoteNodeId = RemoteNodeId;
                RemoteNodeId = handshakeRemoteNodeId;
                Node = new Node(RemoteNodeId, RemoteHost, RemotePort);
            }

            Metrics.Handshakes++;

            HandshakeComplete?.Invoke(this, EventArgs.Empty);
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string? details = null)
        {
            EthDisconnectReason ethDisconnectReason = disconnectReason.ToEthDisconnectReason();

            bool ShouldDisconnectStaticNode()
            {
                return ethDisconnectReason switch
                {
                    EthDisconnectReason.DisconnectRequested or EthDisconnectReason.TcpSubSystemError or EthDisconnectReason.UselessPeer or EthDisconnectReason.TooManyPeers or EthDisconnectReason.Other => false,
                    EthDisconnectReason.ReceiveMessageTimeout or EthDisconnectReason.BreachOfProtocol or EthDisconnectReason.AlreadyConnected or EthDisconnectReason.IncompatibleP2PVersion or EthDisconnectReason.NullNodeIdentityReceived or EthDisconnectReason.ClientQuitting or EthDisconnectReason.UnexpectedIdentity or EthDisconnectReason.IdentitySameAsSelf => true,
                    _ => true,
                };
            }

            if (Node?.IsStatic == true && !ShouldDisconnectStaticNode())
            {
                if (_logger.IsTrace) _logger.Trace($"{this} not disconnecting for static peer on {disconnectReason} ({details})");
                return;
            }

            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (State <= SessionState.HandshakeComplete)
                {
                    if (_disconnectAfterInitialized is not null) return;

                    _disconnectAfterInitialized = (disconnectReason, details);
                    return;
                }

                State = SessionState.DisconnectingProtocols;
            }

            if (_logger.IsDebug) _logger.Debug($"{this} initiating disconnect because {disconnectReason}, details: {details}");
            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (!_protocols.IsEmpty)
            {
                foreach (IProtocolHandler protocolHandler in _protocols.Values)
                {
                    try
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"{this} disconnecting {protocolHandler.Name} {disconnectReason} ({details})");
                        protocolHandler.DisconnectProtocol(disconnectReason, details);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug)
                            _logger.Error($"DEBUG/ERROR Failed to disconnect {protocolHandler.Name} correctly", e);
                    }
                }
            }

            MarkDisconnected(disconnectReason, DisconnectType.Local, details);
        }

        private readonly Lock _sessionStateLock = new();
        public byte P2PVersion { get; private set; }

        private SessionState _state;

        public SessionState State
        {
            get => _state;
            private set
            {
                _state = value;
                BestStateReached = (SessionState)Math.Min((int)SessionState.Initialized, (int)value);
            }
        }

        public SessionState BestStateReached { get; private set; }

        public void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details)
        {
            lock (_sessionStateLock)
            {
                if (State >= SessionState.Disconnecting)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{this} already disconnected {disconnectReason} {disconnectType}");
                    return;
                }

                State = SessionState.Disconnecting;
            }

            if (_isTracked)
            {
                _logger.Warn($"Tracked {this} -> disconnected {disconnectType} {disconnectReason} {details}");
            }

            _disconnectsAnalyzer.ReportDisconnect(disconnectReason, disconnectType, details);

            if (NetworkDiagTracer.IsEnabled && RemoteHost is not null)
                NetworkDiagTracer.ReportDisconnect(Node.Address, $"{disconnectType} {disconnectReason} {details}");

            if (BestStateReached >= SessionState.Initialized && disconnectReason != DisconnectReason.TooManyPeers)
            {
                // TooManyPeers is a benign disconnect that we should not be worried about - many peers are running at their limit
                // also any disconnects before the handshake and init do not have to be logged as they are most likely just rejecting any connections
                if (_logger.IsTrace && HasAgreedCapability(new Capability(Protocol.Eth, 66)) && IsNetworkIdMatched)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"{this} invoking 'Disconnecting' event {disconnectReason} {disconnectType} {details}");
                }
            }
            else
            {
                if (_logger.IsTrace)
                    _logger.Trace($"{this} invoking 'Disconnecting' event {disconnectReason} {disconnectType} {details}");
            }

            Disconnecting?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));

            _ = DisconnectAsync(disconnectType);

            lock (_sessionStateLock)
            {
                State = SessionState.Disconnected;
            }

            if (Disconnected is not null)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"|NetworkTrace| {this} disconnected event {disconnectReason} {disconnectType}");
                Disconnected?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));
            }
            else if (_logger.IsDebug)
                _logger.Error($"DEBUG/ERROR  No subscriptions for session disconnected event on {this}");
        }

        private async Task DisconnectAsync(DisconnectType disconnectType)
        {
            //Possible in case of disconnect before p2p initialization
            if (_context is null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                try
                {
                    await _channel.DisconnectAsync();
                }
                catch (Exception e)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"Error while disconnecting on context on {this} : {e}");
                }
            }
            else
            {
                if (disconnectType == DisconnectType.Local)
                {
                    await Task.Delay(Timeouts.Disconnection);
                }

                try
                {
                    await _context.DisconnectAsync();
                }
                catch (Exception e)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"Error while disconnecting on context on {this} : {e}");
                }
            }
        }

        public event EventHandler<DisconnectEventArgs> Disconnecting;
        public event EventHandler<DisconnectEventArgs> Disconnected;
        public event EventHandler<EventArgs> HandshakeComplete;
        public event EventHandler<EventArgs> Initialized;
        public event EventHandler<PeerEventArgs> MsgReceived;
        public event EventHandler<PeerEventArgs> MsgDelivered;

        public void Dispose()
        {
            lock (_sessionStateLock)
            {
                if (State != SessionState.Disconnected)
                {
                    throw new InvalidOperationException($"Disposing {this}");
                }
            }

            foreach ((_, IProtocolHandler handler) in _protocols)
            {
                handler.Dispose();
            }
        }

        private IPacketSender _packetSender;

        public void AddProtocolHandler(IProtocolHandler handler)
        {
            if (handler.ProtocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException(
                    $"{Protocol.P2P} handler has to be started before starting {handler.ProtocolCode} handler on {this}");
            }

            if (!_protocols.TryAdd(handler.ProtocolCode, handler))
            {
                throw new InvalidOperationException($"{this} already has {handler.ProtocolCode} started");
            }

            _resolver = GetOrCreateResolver();
        }

        private AdaptiveCodeResolver GetOrCreateResolver()
        {
            string key = string.Join(":", _protocols.Select(static p => p.Value.Name).OrderBy(static x => x));
            if (!_resolvers.TryGetValue(key, out AdaptiveCodeResolver value))
            {
                value = _resolvers.AddOrUpdate(
                    key,
                    addValueFactory: (k) => new AdaptiveCodeResolver(_protocols),
                    updateValueFactory: (k, v) => v);
            }

            return value;
        }

        public override string ToString()
        {
            string formattedRemoteHost = RemoteHost?.Replace("::ffff:", string.Empty);
            return Direction == ConnectionDirection.In
                ? $"[Session|{Direction}|{State}|{formattedRemoteHost}:{RemotePort}->{LocalPort}]"
                : $"[Session|{Direction}|{State}|{LocalPort}->{formattedRemoteHost}:{RemotePort}]";
        }

        private AdaptiveCodeResolver _resolver;

        private class AdaptiveCodeResolver
        {
            private readonly (string ProtocolCode, int SpaceSize)[] _alphabetically;

            public AdaptiveCodeResolver(IDictionary<string, IProtocolHandler> protocols)
            {
                _alphabetically = new (string, int)[protocols.Count];
                _alphabetically[0] = (Protocol.P2P, protocols[Protocol.P2P].MessageIdSpaceSize);
                int i = 1;
                foreach (KeyValuePair<string, IProtocolHandler> protocolSession
                    in protocols.Where(static kv => kv.Key != Protocol.P2P).OrderBy(static kv => kv.Key))
                {
                    _alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
                }
            }

            public (string, int) ResolveProtocol(int adaptiveId)
            {
                int offset = 0;
                for (int j = 0; j < _alphabetically.Length; j++)
                {
                    if (offset + _alphabetically[j].SpaceSize > adaptiveId)
                    {
                        return (_alphabetically[j].ProtocolCode, adaptiveId - offset);
                    }

                    offset += _alphabetically[j].SpaceSize;
                }

                // consider disconnecting on the breach of protocol here?
                return (null, 0);
            }

            public int ResolveAdaptiveId(string protocol, int messageCode)
            {
                int offset = 0;
                for (int j = 0; j < _alphabetically.Length; j++)
                {
                    if (_alphabetically[j].ProtocolCode == protocol)
                    {
                        if (_alphabetically[j].SpaceSize <= messageCode)
                        {
                            break;
                        }

                        return offset + messageCode;
                    }

                    offset += _alphabetically[j].SpaceSize;
                }

                throw new InvalidOperationException(
                    $"Registered protocols do not support {protocol} with message code {messageCode}. " +
                    $"Registered: {string.Join(";", _alphabetically)}."
                );
            }
        }

        private bool _isTracked = false;

        public void StartTrackingSession()
        {
            _isTracked = true;
        }

        private void RecordOutgoingMessageMetric<T>(T message, int size) where T : P2PMessage
        {
            byte version = _protocols.TryGetValue(message.Protocol, out IProtocolHandler? handler)
                ? handler!.ProtocolVersion
                : (byte)0;

            P2PMessageKey metricKey = new P2PMessageKey(new VersionedProtocol(message.Protocol, version), message.PacketType);
            Metrics.OutgoingP2PMessages.AddOrUpdate(metricKey, 0, IncrementMetric);
            Metrics.OutgoingP2PMessageBytes.AddOrUpdate(metricKey, ZeroMetric, AddMetric, size);
        }

        private void RecordIncomingMessageMetric(string protocol, int packetType, int size)
        {
            if (protocol is null) return;
            byte version = _protocols.TryGetValue(protocol, out IProtocolHandler? handler)
                ? handler!.ProtocolVersion
                : (byte)0;
            P2PMessageKey metricKey = new P2PMessageKey(new VersionedProtocol(protocol, version), packetType);
            Metrics.IncomingP2PMessages.AddOrUpdate(metricKey, 0, IncrementMetric);
            Metrics.IncomingP2PMessageBytes.AddOrUpdate(metricKey, ZeroMetric, AddMetric, size);
        }

        private static long IncrementMetric(P2PMessageKey _, long value)
        {
            return value + 1;
        }

        private static long ZeroMetric(P2PMessageKey _, int i)
        {
            return 0;
        }

        private static long AddMetric(P2PMessageKey _, long value, int toAdd)
        {
            return value + toAdd;
        }
    }
}
