/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DarkRift.Server.Plugins.Listeners.Bichannel
{
    [Obsolete("Use the BichannelListener instead")]
    internal sealed class CompatibilityBichannelListener : BichannelListenerBase
    {
        public override Version Version => new Version(1, 0, 0);
        
        public CompatibilityBichannelListener(NetworkListenerLoadData listenerLoadData) : base(listenerLoadData)
        {
        }

        public override void StartListening()
        {
            BindSockets();

            Logger.Trace("Starting compatibility listener.");

            TcpListener.BeginAccept(TcpAcceptCompleted, null);

            EndPoint remoteEndPoint = new IPEndPoint(Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            byte[] buffer = new byte[ushort.MaxValue];
            UdpListener.BeginReceiveFrom(buffer, 0, ushort.MaxValue, SocketFlags.None, ref remoteEndPoint, UdpMessageReceived, buffer);

            Logger.Info($"Server mounted, listening on port {Port}{(UdpPort != Port ? "|" + UdpPort : "")}.");
            Logger.Warning("The CompatibilityBichannelListener is now obsolete an not recommended for use. Instead, you should update your configuration to use the BichannelListener instead." +
                "\n\nYou can continue using the CompatibilityBichannelListener until it is removed in a future version of DarkRift. If you are using a version of Unity that the " +
                "BichannelListener does not support you should consider upgrading your Unity version.");
        }

        /// <summary>
        ///     Called when a new client has been accepted through the fallback accept.
        /// </summary>
        /// <param name="result">The result of the accept.</param>
        private void TcpAcceptCompleted(IAsyncResult result)
        {
            Socket socket;
            try
            {
                socket = TcpListener.EndAccept(result);
            }
            catch
            {
                return;
            }

            try
            {
                HandleTcpConnection(socket);
            }
            finally
            {
                TcpListener.BeginAccept(TcpAcceptCompleted, null);
            }
        }

        /// <summary>
        ///     Called when a UDP message is received on the fallback system.
        /// </summary>
        /// <param name="result">The result of the operation.</param>
        private void UdpMessageReceived(IAsyncResult result)
        {
            EndPoint remoteEndPoint = new IPEndPoint(Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            int bytesReceived;
            try
            {
                bytesReceived = UdpListener.EndReceiveFrom(result, ref remoteEndPoint);
            }
            catch (SocketException)
            {
                UdpListener.BeginReceiveFrom((byte[])result.AsyncState, 0, ushort.MaxValue, SocketFlags.None, ref remoteEndPoint, UdpMessageReceived, (byte[])result.AsyncState);
                return;
            }

            //Copy over buffer and remote endpoint
            using (MessageBuffer buffer = MessageBuffer.Create(bytesReceived))
            {
                Buffer.BlockCopy((byte[])result.AsyncState, 0, buffer.Buffer, buffer.Offset, bytesReceived);
                buffer.Count = bytesReceived;

                //Start listening again
                UdpListener.BeginReceiveFrom((byte[])result.AsyncState, 0, ushort.MaxValue, SocketFlags.None, ref remoteEndPoint, UdpMessageReceived, (byte[])result.AsyncState);

                //Handle message or new connection
                BichannelServerConnection connection;
                bool exists;
                lock (UdpConnections)
                    exists = UdpConnections.TryGetValue(remoteEndPoint, out connection);

                if (exists)
                    connection.HandleUdpMessage(buffer);
                else
                    HandleUdpConnection(buffer, remoteEndPoint);
            }
        }

        internal override bool SendUdpBuffer(EndPoint remoteEndPoint, MessageBuffer message, Action<int, SocketError> completed)
        {
            //disabling the DarkRift 2 BeginSendTo because
            //A) Unity's GC sucks and SendTo creates marginally less garbage than BeginSendTo, although unfortunately not zero due to a bad Mono sockets implementation that allocates in IP address handling
            //B) queued UDP operations are basically pointless from a realtime perspective (consider e.g. snapshots)
            //C) unbounded buffers tend to blow up under stress
            
            using (message)
            {
                int bytesSent = 0;
                SocketError result = SocketError.Success;
                try
                {
                    bytesSent = UdpListener.SendTo(message.Buffer, message.Offset, message.Count, SocketFlags.None, remoteEndPoint);
                }
                catch (SocketException e)
                {
                    result = e.SocketErrorCode;
                }

                completed(bytesSent, result);
            }

            return true;
        }
    }
}
