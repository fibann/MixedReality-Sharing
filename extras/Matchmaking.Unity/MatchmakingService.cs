// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if UNITY_ANDROID && !UNITY_EDITOR
#   define ANDROID_DEVICE
#endif

using Microsoft.MixedReality.Sharing.Matchmaking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Microsoft.MixedReality.SpectatorView
{
    /// <summary>Wraps a `PeerMatchmakingService` based on UDP broadcast.</summary>
    /// <remarks>
    /// Exposes the main functions and configuration options while handling platform-specific details.
    /// </remarks>
    public class MatchmakingService : MonoBehaviour
    {
        /// <summary>
        /// Configuration for the matchmaking service.
        /// </summary>
        [Serializable]
        public class Options
        {
            /// <summary>Port used to send and receive broadcast packets.</summary>
            [Tooltip("Port used to send and receive broadcast packets.")]
            public ushort BroadcastPort = 17410;

            /// <summary>
            /// Address used to send and receive broadcast packets. Should be an IP broadcast or multicast address.
            /// If null, 255.255.255.255 will be used.
            /// </summary>
            [Tooltip("Address used to send and receive broadcast packets. " +
                "Should be an IP broadcast or multicast address. If null, 255.255.255.255 will be used.")]
            public string BroadcastAddress;

            /// <summary>
            /// Local address to bind to. Can be null if the service should not bind to a specific address.
            /// </summary>
            [Tooltip("Local address to bind to. Can be null if the service should not bind to a specific address.")]
            public string LocalAddress;
        }

        [Tooltip("Configuration for the matchmaking service.")]
        [SerializeField]
        private Options _options = new Options();

        /// <summary>
        /// Configuration for the matchmaking service. Can only be set before the component is enabled.
        /// </summary>
        public Options OptionValues
        {
            get => _options;
            set
            {
                if (_mmService != null)
                {
                    Debug.LogError("Cannot set options after MatchmakingService has been enabled.");
                }
                else
                {
                    _options = value;
                }
            }
        }

        private IMatchmakingService _mmService;

#if ANDROID_DEVICE
        // MulticastLock used to keep receiving broadcast UDP queries/announcements.
        private AndroidJavaObject _mcastLock;

        private void Awake()
        {
            // Create the MulticastLock.
            using (AndroidJavaObject activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity"))
            using (var wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
            {
                _mcastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "MatchmakingMulticastLock");
            }
        }

        private void OnDestroy()
        {
            _mcastLock.Dispose();
            _mcastLock = null;
        }
#endif

        // Starts the matchmaking service.
        private void OnEnable()
        {
            // Set broadcast address.
            IPAddress bcastAddress = IPAddress.Broadcast;
            if (!string.IsNullOrEmpty(_options.BroadcastAddress))
            {
                if (!IPAddress.TryParse(_options.BroadcastAddress, out bcastAddress))
                {
                    Debug.LogError($"{_options.BroadcastAddress} is not a valid address");
                    return;
                }
            }

            // Set local address.
            IPAddress localAddress = bcastAddress.AddressFamily == AddressFamily.InterNetwork ?
                IPAddress.Any : IPAddress.IPv6Any;
            if (string.IsNullOrEmpty(_options.LocalAddress))
            {
#if UNITY_WSA
                // On UWP, sockets bound to INADDR_ANY won't be able to send messages to INADDR_BROADCAST,
                // so we need to always bind the socket to a specific address.
                var goodAddress = SocketerClient.GetLocalIPAddress();
                if (goodAddress == null)
                {
                    Debug.LogError($"Could not find a valid local address to bind");
                    return;
                }
                localAddress = IPAddress.Parse(goodAddress);
#endif
            }
            else
            {
                if (!IPAddress.TryParse(_options.LocalAddress, out localAddress))
                {
                    Debug.LogError($"{_options.LocalAddress} is not a valid local address");
                    return;
                }
            }


            Debug.Log($"Starting matchmaking service, binding to {localAddress}," +
                $" broadcasting to {bcastAddress}, on port {_options.BroadcastPort}");
            var network = new UdpPeerNetwork(bcastAddress, _options.BroadcastPort, localAddress);
#if ANDROID_DEVICE
            // Acquire the MulticastLock when the network is active.
            network.Started += _ =>
            {
                _mcastLock.Call("acquire");
            };
            network.Stopped += _ =>
            {
                _mcastLock.Call("release");
            };
#endif
            _mmService = new PeerMatchmakingService(network);
        }

        /// <summary>
        /// Create a room. See <see cref="IMatchmakingService.CreateRoomAsync(string, string, IReadOnlyDictionary{string, string}, CancellationToken)"/>.
        /// </summary>
        public Task<IRoom> CreateRoomAsync(
            string category,
            string connection,
            IReadOnlyDictionary<string, string> attributes = null,
            CancellationToken token = default)
        {
            if (_mmService == null)
            {
                Debug.LogError($"Cannot create room ({category}, {connection}): MatchmakingService is not initialized");
                return null;
            }

            Debug.Log($"Creating room ({category}, {connection})");
            return _mmService.CreateRoomAsync(category, connection, attributes, token)
                .ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        Debug.Log($"Room ({category}, {connection}) created");
                        return task.Result;
                    }
                    Debug.LogError($"Failed to create room ({category}, {connection}): {task.Exception.InnerException.Message}");
                    throw task.Exception.InnerException;
                }, token);
        }

        /// <summary>
        /// Start discovering rooms. See <see cref="IMatchmakingService.StartDiscovery(string)"/>.
        /// </summary>
        public IDiscoveryTask StartDiscovery(string category)
        {
            if (_mmService == null)
            {
                Debug.LogError($"Cannot start discovery of category {category}: MatchmakingService is not initialized");
                return null;
            }

            Debug.Log($"Start discovery of category {category}");
            return _mmService.StartDiscovery(category);
        }

        private void OnDisable()
        {
            _mmService.Dispose();
            _mmService = null;
        }
    }
}
