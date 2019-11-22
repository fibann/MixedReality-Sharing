// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.MixedReality.Sharing.Matchmaking;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Microsoft.MixedReality.SpectatorView
{
    /// <summary>
    /// Wrapper for a <see cref="IDiscoveryTask"/>. Dispatches room updates during the <see cref="Update"/> method.
    /// Starts the discovery when enabled if <see cref="MatchmakingService"/> is set. Otherwise, the discovery can
    /// be started manually through <see cref="StartDiscovery"/>.
    /// </summary>
    public class RoomDiscovery : MonoBehaviour
    {
        /// <summary>
        /// Matchmaking service used for room discovery.
        /// </summary>
        [Tooltip("Matchmaking service used for room discovery.")]
        public MatchmakingService MatchmakingService = null;

        /// <summary>
        /// Room category to discover.
        /// </summary>
        [Tooltip("Room category to discover.")]
        public string Category;

        private IDiscoveryTask _discoveryTask = null;
        private volatile bool _roomsHaveBeenUpdated = false;

        /// <summary>
        /// Called during <see cref="Update"/> every time that rooms are updated.
        /// Handlers are released when this object is destroyed.
        /// </summary>
        public event Action<IEnumerable<IRoom>> RoomsFound;

        /// <summary>
        /// Start the discovery of the selected category. <see cref="RoomsFound"/> will be called on the Update loop
        /// when new rooms are detected.
        /// </summary>
        public void StartDiscovery()
        {
            if (MatchmakingService == null)
            {
                Debug.LogError($"Cannot start discovery of category {Category}: {nameof(MatchmakingService)} is not set");
                return;
            }

            if (_discoveryTask != null)
            {
                Debug.LogError($"Cannot start discovery of category {Category}: a discovery is already in progress.");
                return;
            }

            _discoveryTask = MatchmakingService.StartDiscovery(Category);
            _discoveryTask.Updated += (task) => { _roomsHaveBeenUpdated = true; };
        }

        /// <summary>
        /// Stop the discovery of the selected category.
        /// </summary>
        public void StopDiscovery()
        {
            if (_discoveryTask != null)
            {
                _discoveryTask.Dispose();
                _discoveryTask = null;
            }
        }

        private void OnEnable()
        {
            if (MatchmakingService)
            {
                StartDiscovery();
            }
        }

        private void Update()
        {
            if (_discoveryTask != null && _roomsHaveBeenUpdated)
            {
                _roomsHaveBeenUpdated = false;
                RoomsFound?.Invoke(_discoveryTask.Rooms);
            }

        }

        private void OnDisable()
        {
            StopDiscovery();
        }

        private void OnDestroy()
        {
            RoomsFound = null;
        }
    }
}
