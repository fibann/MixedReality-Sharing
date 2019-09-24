﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.MixedReality.Sharing.Matchmaking
{
    class UdpPeerNetworkMessage : IPeerNetworkMessage
    {
        public ArraySegment<byte> Contents { get; }
        internal UdpPeerNetworkMessage(EndPoint sender, ArraySegment<byte> msg)
        {
            Contents = msg;
            sender_ = sender;
        }
        internal EndPoint sender_;
    }

    public class UdpPeerNetwork : IPeerNetwork
    {
        private const int HeaderSize = sizeof(int); //< sequence number

        private Socket socket_;
        private readonly IPEndPoint broadcastEndpoint_;
        private readonly IPAddress localAddress_;
        private readonly EndPoint anywhere_ = new IPEndPoint(IPAddress.Any, 0);
        private readonly byte[] readBuffer_ = new byte[1024];
        private readonly ArraySegment<byte> readSegment_;

        private int lastSent_ = 0;

        private class LastReceivedInfo
        {
            public int LastReceived = 0;
            public DateTime LastHeard = DateTime.UtcNow; //< TODO use to purge old entries
        }

        // Map the ID of each stream for which we are receiving messages to the highest seen sequence number.
        private readonly Dictionary<EndPoint, LastReceivedInfo> infoFromEndPoint_ = new Dictionary<EndPoint, LastReceivedInfo>();

        public event Action<IPeerNetwork, IPeerNetworkMessage> Message;

        /// <summary>
        /// Create a new network.
        /// </summary>
        /// <param name="broadcast">Broadcast or multicast address used to send packets to other hosts.</param>
        /// <param name="local">Local address. TODO.</param>
        /// <param name="port">Port used to send and receive broadcast packets.</param>
        public UdpPeerNetwork(IPAddress broadcast, ushort port, IPAddress local = null)
        {
            broadcastEndpoint_ = new IPEndPoint(broadcast, port);
            localAddress_ = local ?? AnyAddress(broadcast.AddressFamily);
            readSegment_ = new ArraySegment<byte>(readBuffer_);
        }

        private IPAddress AnyAddress(AddressFamily family)
        {
            switch (family)
            {
                case AddressFamily.InterNetwork:
                    return IPAddress.Any;
                case AddressFamily.InterNetworkV6:
                    return IPAddress.IPv6Any;
                default:
                    throw new ArgumentException($"Invalid family: {family}");
            }
        }

        private void HandleAsyncRead(Task<SocketReceiveFromResult> task)
        {
            if (task.IsFaulted)
            {
                task.Exception.Handle(e =>
                {
                    // If the socket has been closed, quit gracefully.
                    return e is ObjectDisposedException;
                });
                return;
            }

            // Dispatch the message.
            var result = task.Result;
            Debug.Assert(readSegment_.Offset == 0);

            int seqNum;
            using (var str = new MemoryStream(readSegment_.Array, readSegment_.Offset, readSegment_.Count, false))
            using (var reader = new BinaryReader(str))
            {
                seqNum = reader.ReadInt32();
            }

            bool handleMessage = false;
            if (infoFromEndPoint_.TryGetValue(result.RemoteEndPoint, out LastReceivedInfo recInfo))
            {
                if (seqNum > recInfo.LastReceived)
                {
                    handleMessage = true;
                    recInfo.LastReceived = seqNum;
                    recInfo.LastHeard = DateTime.UtcNow;
                }
            }
            else
            {
                handleMessage = true;
                infoFromEndPoint_.Add(result.RemoteEndPoint, new LastReceivedInfo { LastReceived = seqNum });
            }

            if (handleMessage)
            {
                Message?.Invoke(this, new UdpPeerNetworkMessage(result.RemoteEndPoint,
                    new ArraySegment<byte>(readSegment_.Array, HeaderSize, result.ReceivedBytes)));
            }

            // Listen again.
            socket_.ReceiveFromAsync(readSegment_, SocketFlags.None, anywhere_)
                .ContinueWith(HandleAsyncRead);
        }

        public void Start()
        {
            Debug.Assert(socket_ == null);
            socket_ = new Socket(broadcastEndpoint_.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            // Bind to the broadcast port.
            socket_.Bind(new IPEndPoint(localAddress_, broadcastEndpoint_.Port));

            // Optionally join a multicast group
            if (IsMulticast(broadcastEndpoint_.Address))
            {
                if (broadcastEndpoint_.AddressFamily == AddressFamily.InterNetwork)
                {
                    MulticastOption mcastOption;
                    mcastOption = new MulticastOption(broadcastEndpoint_.Address, localAddress_);

                    socket_.SetSocketOption(SocketOptionLevel.IP,
                                                SocketOptionName.AddMembership,
                                                mcastOption);
                }
                else if (broadcastEndpoint_.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    IPv6MulticastOption mcastOption;
                    if (localAddress_ != IPAddress.IPv6Any)
                    {
                        var ifaceIdx = GetIfaceIdxFromAddress(localAddress_);
                        mcastOption = new IPv6MulticastOption(broadcastEndpoint_.Address, ifaceIdx);
                    }
                    else
                    {
                        mcastOption = new IPv6MulticastOption(broadcastEndpoint_.Address);
                    }
                    socket_.SetSocketOption(SocketOptionLevel.IP,
                                                SocketOptionName.AddMembership,
                                                mcastOption);
                }
                else
                {
                    // Should never happen
                    throw new NotSupportedException($"Invalid address family: {broadcastEndpoint_.AddressFamily}");
                }
            }
            else
            {
                // Assume this is a broadcast address.
                socket_.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
            }

            socket_.ReceiveFromAsync(readSegment_, SocketFlags.None, anywhere_)
                .ContinueWith(HandleAsyncRead);
        }

        private static bool IsMulticast(IPAddress address)
        {
            return address.IsIPv6Multicast ||
                (address.AddressFamily == AddressFamily.InterNetwork &&
                (address.GetAddressBytes()[0] >> 4 == 14));
        }

        private static long GetIfaceIdxFromAddress(IPAddress localAddress)
        {
            var ifaces = NetworkInterface.GetAllNetworkInterfaces();
            var found = ifaces.First(iface =>
                        iface.GetIPProperties().UnicastAddresses.Any(
                            addressInfo => addressInfo.Address.Equals(localAddress)));
            return found.GetIPProperties().GetIPv6Properties().Index;
        }

        public void Stop()
        {
            socket_.Dispose();
        }

        // Prepend stream ID and sequence number.
        byte[] PrependHeader(ArraySegment<byte> message)
        {
            int size = HeaderSize + message.Count;
            var res = new byte[size];
            int seqId = Interlocked.Increment(ref lastSent_);
            using (var str = new MemoryStream(res))
            using (var writer = new BinaryWriter(str))
            {
                writer.Write(seqId);
                writer.Write(message.Array, message.Offset, message.Count);
            }
            return res;
        }

        public void Broadcast(ArraySegment<byte> message)
        {
            var buffer = PrependHeader(message);
            socket_.SendTo(buffer, SocketFlags.None, broadcastEndpoint_);
        }

        public void Reply(IPeerNetworkMessage req, ArraySegment<byte> message)
        {
            var umsg = req as UdpPeerNetworkMessage;
            var buffer = PrependHeader(message);
            socket_.SendTo(buffer, SocketFlags.None, umsg.sender_);
        }
    }
}
