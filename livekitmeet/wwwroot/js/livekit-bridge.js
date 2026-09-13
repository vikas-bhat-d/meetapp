/**
 * LiveKit Client JS Bridge for Blazor
 * 
 * Manages WebRTC media streams, audio/video elements, and the isolated DOM grid.
 * Keeps video/audio rendering separate from Blazor's Virtual DOM.
 */

window.livekitBridge = {
    activeRoom: null,
    dotNetRef: null,
    gridContainer: null,
    audioContainer: null,
    participants: new Map(), // identity -> { participant, tileEl, videoEl, avatarEl, micEl }

    /**
     * Acquire one microphone track and let LiveKit publish that same track.
     * Opening a probe stream and then opening the microphone again can make
     * Android WebView report "Could not start audio source".
     */
    async _createMicrophoneTrack() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            throw new Error('Microphone capture is not available in this WebView.');
        }

        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        const mediaTrack = stream.getAudioTracks()[0];
        if (!mediaTrack) {
            stream.getTracks().forEach(track => track.stop());
            throw new Error('The WebView did not return a microphone track.');
        }

        const LocalAudioTrack = window.LivekitClient.LocalAudioTrack;
        if (!LocalAudioTrack) {
            mediaTrack.stop();
            throw new Error('LiveKit microphone track support is not available.');
        }

        return new LocalAudioTrack(mediaTrack);
    },

    _notifyMediaError(mediaType, error) {
        const message = error && error.message ? error.message : String(error);
        console.warn(`[LiveKitBridge] Could not enable ${mediaType}:`, message);
        window.ReactNativeWebView?.postMessage(JSON.stringify({
            type: 'media-error',
            mediaType,
            message
        }));
        this.dotNetRef?.invokeMethodAsync('OnMediaError', mediaType, message).catch(() => undefined);
    },

    async _publishMicrophoneTrack(participant) {
        const microphoneTrack = await this._createMicrophoneTrack();
        const microphoneSource = window.LivekitClient.Track?.Source?.Microphone;
        const publishOptions = microphoneSource ? { source: microphoneSource } : undefined;
        await participant.publishTrack(microphoneTrack, publishOptions);
    },

    /**
     * Join a LiveKit room
     */
    async joinRoom(containerId, serverUrl, token, dotNetHelper, userChoices) {
        if (!window.LivekitClient) {
            console.error('[LiveKitBridge] LivekitClient library is not loaded!');
            return { success: false, error: 'LivekitClient library is not loaded.' };
        }

        try {
            // Clean up any existing room
            await this.leaveRoom();

            this.dotNetRef = dotNetHelper;
            this.gridContainer = document.getElementById(containerId);

            if (!this.gridContainer) {
                throw new Error(`Container with id '${containerId}' not found.`);
            }

            // Clear container
            this.gridContainer.innerHTML = '';
            this.participants.clear();

            // Hidden audio container for remote audio tracks
            let audioCont = document.getElementById('lk-audio-container');
            if (!audioCont) {
                audioCont = document.createElement('div');
                audioCont.id = 'lk-audio-container';
                audioCont.style.display = 'none';
                document.body.appendChild(audioCont);
            }
            audioCont.innerHTML = '';
            this.audioContainer = audioCont;

            // Initialize Room
            const room = new window.LivekitClient.Room({
                adaptiveStream: true,
                dynacast: true,
                videoCaptureDefaults: {
                    resolution: window.LivekitClient.VideoPresets.h720.resolution
                }
            });
            this.activeRoom = room;

            // Setup Room Event Listeners
            this._setupRoomEvents(room);

            // Connect to LiveKit server
            console.log(`[LiveKitBridge] Connecting to ${serverUrl}...`);
            await room.connect(serverUrl, token, { autoSubscribe: true });
            console.log(`[LiveKitBridge] Connected to room: ${room.name}`);

            // Add local participant tile immediately
            this._createParticipantTile(room.localParticipant, true);

            // Publish microphone / camera according to user choices
            if (userChoices && userChoices.videoEnabled) {
                try {
                    await room.localParticipant.setCameraEnabled(true);
                } catch (e) {
                    console.warn('[LiveKitBridge] Could not enable camera:', e);
                }
            }
            if (userChoices && userChoices.audioEnabled) {
                try {
                    await this._publishMicrophoneTrack(room.localParticipant);
                } catch (e) {
                    this._notifyMediaError('microphone', e);
                }
            }

            // Existing remote participants
            room.remoteParticipants.forEach(remotePart => {
                this._createParticipantTile(remotePart, false);
                // Attach tracks if already subscribed
                remotePart.trackPublications.forEach(pub => {
                    if (pub.isSubscribed && pub.track) {
                        this._handleTrackSubscribed(pub.track, pub, remotePart);
                    }
                });
            });

            this._updateGridLayout();
            this._notifyParticipants();

            if (this.dotNetRef) {
                await this.dotNetRef.invokeMethodAsync(
                    'OnRoomConnected',
                    room.name,
                    room.localParticipant.identity
                );
                await this.dotNetRef.invokeMethodAsync(
                    'OnTrackStateChanged',
                    room.localParticipant.isMicrophoneEnabled,
                    room.localParticipant.isCameraEnabled
                );
            }

            return { success: true, roomName: room.name };
        } catch (error) {
            console.error('[LiveKitBridge] Error joining room:', error);
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnConnectionError', error.message || error.toString());
            }
            return { success: false, error: error.message };
        }
    },

    /**
     * Wire up Room events
     */
    _setupRoomEvents(room) {
        const LK = window.LivekitClient;

        room.on(LK.RoomEvent.ParticipantConnected, (participant) => {
            console.log(`[LiveKitBridge] Participant joined: ${participant.identity}`);
            this._createParticipantTile(participant, false);
            this._updateGridLayout();
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.ParticipantDisconnected, (participant) => {
            console.log(`[LiveKitBridge] Participant left: ${participant.identity}`);
            this._removeParticipantTile(participant.identity);
            this._updateGridLayout();
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.TrackSubscribed, (track, publication, participant) => {
            console.log(`[LiveKitBridge] Track subscribed: ${track.kind} from ${participant.identity}`);
            this._handleTrackSubscribed(track, publication, participant);
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.TrackUnsubscribed, (track, publication, participant) => {
            console.log(`[LiveKitBridge] Track unsubscribed: ${track.kind} from ${participant.identity}`);
            this._handleTrackUnsubscribed(track, publication, participant);
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.TrackMuted, (publication, participant) => {
            this._handleTrackMuteState(publication, participant, true);
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.TrackUnmuted, (publication, participant) => {
            this._handleTrackMuteState(publication, participant, false);
            this._notifyParticipants();
        });

        room.on(LK.RoomEvent.LocalTrackPublished, (publication, participant) => {
            if (publication.track && publication.track.kind === 'video') {
                const partInfo = this.participants.get(participant.identity);
                if (partInfo && partInfo.videoEl) {
                    publication.track.attach(partInfo.videoEl);
                    partInfo.videoEl.style.display = 'block';
                    partInfo.avatarEl.style.display = 'none';
                }
            }
            this._updateLocalState();
        });

        room.on(LK.RoomEvent.LocalTrackUnpublished, (publication, participant) => {
            if (publication.track && publication.track.kind === 'video') {
                const partInfo = this.participants.get(participant.identity);
                if (partInfo) {
                    publication.track.detach();
                    if (partInfo.videoEl) partInfo.videoEl.style.display = 'none';
                    if (partInfo.avatarEl) partInfo.avatarEl.style.display = 'flex';
                }
            }
            this._updateLocalState();
        });

        room.on(LK.RoomEvent.ActiveSpeakersChanged, (speakers) => {
            const activeIds = new Set(speakers.map(s => s.identity));
            this.participants.forEach((info, identity) => {
                if (activeIds.has(identity)) {
                    info.tileEl.classList.add('speaking');
                } else {
                    info.tileEl.classList.remove('speaking');
                }
            });
        });

        room.on(LK.RoomEvent.Disconnected, () => {
            console.log('[LiveKitBridge] Room disconnected.');
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnDisconnected');
            }
        });
    },

    /**
     * Create isolated participant tile DOM
     */
    _createParticipantTile(participant, isLocal) {
        if (!this.gridContainer) return;
        if (this.participants.has(participant.identity)) return;

        const tile = document.createElement('div');
        tile.className = 'lk-participant-tile';
        tile.id = `lk-tile-${participant.identity}`;

        const videoWrapper = document.createElement('div');
        videoWrapper.className = 'lk-video-wrapper';

        // Video element
        const video = document.createElement('video');
        video.className = `lk-video ${isLocal ? 'local-video' : ''}`;
        video.autoplay = true;
        video.playsInline = true;
        if (isLocal) {
            video.muted = true; // Local video must always be muted to avoid feedback loop
        }
        video.style.display = 'none';

        // Avatar placeholder (visible when camera is off)
        const avatar = document.createElement('div');
        avatar.className = 'lk-avatar-placeholder';
        const initial = (participant.name || participant.identity || '?').trim().charAt(0).toUpperCase();
        avatar.innerHTML = `<div class="lk-avatar-circle">${initial}</div>`;
        avatar.style.display = 'flex';

        videoWrapper.appendChild(video);
        videoWrapper.appendChild(avatar);

        // Tile footer overlay
        const footer = document.createElement('div');
        footer.className = 'lk-tile-footer';

        const nameSpan = document.createElement('span');
        nameSpan.className = 'lk-participant-name';
        nameSpan.textContent = (participant.name || participant.identity) + (isLocal ? ' (You)' : '');

        const micSpan = document.createElement('span');
        micSpan.className = 'lk-mic-status unmuted';
        micSpan.innerHTML = '🎤';

        footer.appendChild(nameSpan);
        footer.appendChild(micSpan);

        tile.appendChild(videoWrapper);
        tile.appendChild(footer);

        this.gridContainer.appendChild(tile);

        this.participants.set(participant.identity, {
            participant,
            isLocal,
            tileEl: tile,
            videoEl: video,
            avatarEl: avatar,
            micEl: micSpan
        });
    },

    /**
     * Remove participant tile
     */
    _removeParticipantTile(identity) {
        const info = this.participants.get(identity);
        if (info) {
            if (info.tileEl && info.tileEl.parentNode) {
                info.tileEl.parentNode.removeChild(info.tileEl);
            }
            this.participants.delete(identity);
        }
    },

    /**
     * Handle remote track subscribed
     */
    _handleTrackSubscribed(track, publication, participant) {
        if (track.kind === 'video') {
            const partInfo = this.participants.get(participant.identity);
            if (partInfo && partInfo.videoEl) {
                track.attach(partInfo.videoEl);
                partInfo.videoEl.style.display = 'block';
                partInfo.avatarEl.style.display = 'none';
            }
        } else if (track.kind === 'audio') {
            const audioEl = track.attach();
            audioEl.dataset.participant = participant.identity;
            if (this.audioContainer) {
                this.audioContainer.appendChild(audioEl);
            }
        }
    },

    /**
     * Handle track unsubscribed
     */
    _handleTrackUnsubscribed(track, publication, participant) {
        track.detach();
        if (track.kind === 'video') {
            const partInfo = this.participants.get(participant.identity);
            if (partInfo) {
                if (partInfo.videoEl) partInfo.videoEl.style.display = 'none';
                if (partInfo.avatarEl) partInfo.avatarEl.style.display = 'flex';
            }
        } else if (track.kind === 'audio') {
            if (this.audioContainer) {
                const els = this.audioContainer.querySelectorAll(`[data-participant="${participant.identity}"]`);
                els.forEach(el => el.remove());
            }
        }
    },

    /**
     * Handle track muted/unmuted state
     */
    _handleTrackMuteState(publication, participant, isMuted) {
        const partInfo = this.participants.get(participant.identity);
        if (!partInfo) return;

        if (publication.kind === 'video') {
            if (isMuted) {
                if (partInfo.videoEl) partInfo.videoEl.style.display = 'none';
                if (partInfo.avatarEl) partInfo.avatarEl.style.display = 'flex';
            } else {
                if (partInfo.videoEl) partInfo.videoEl.style.display = 'block';
                if (partInfo.avatarEl) partInfo.avatarEl.style.display = 'none';
            }
        } else if (publication.kind === 'audio') {
            if (partInfo.micEl) {
                partInfo.micEl.className = `lk-mic-status ${isMuted ? 'muted' : 'unmuted'}`;
                partInfo.micEl.innerHTML = isMuted ? '🔇' : '🎤';
            }
        }
    },

    /**
     * Adjust responsive CSS grid classes
     */
    _updateGridLayout() {
        if (!this.gridContainer) return;
        const count = this.participants.size;
        this.gridContainer.className = 'livekit-video-grid';

        if (count <= 1) {
            this.gridContainer.classList.add('count-1');
        } else if (count === 2) {
            this.gridContainer.classList.add('count-2');
        } else if (count <= 4) {
            this.gridContainer.classList.add('count-4');
        } else {
            this.gridContainer.classList.add('count-more');
        }
    },

    /**
     * Notify Blazor of updated participant list
     */
    _notifyParticipants() {
        if (!this.dotNetRef) return;
        const list = [];
        this.participants.forEach((info, identity) => {
            const p = info.participant;
            list.push({
                identity: identity,
                name: p.name || identity,
                isLocal: info.isLocal,
                isAudioEnabled: p.isMicrophoneEnabled,
                isVideoEnabled: p.isCameraEnabled,
                isSpeaking: info.tileEl ? info.tileEl.classList.contains('speaking') : false
            });
        });
        this.dotNetRef.invokeMethodAsync('OnParticipantsUpdated', list);
    },

    /**
     * Notify Blazor of local track state changes
     */
    _updateLocalState() {
        if (!this.dotNetRef || !this.activeRoom) return;
        const local = this.activeRoom.localParticipant;
        this.dotNetRef.invokeMethodAsync(
            'OnTrackStateChanged',
            local.isMicrophoneEnabled,
            local.isCameraEnabled
        );
    },

    /**
     * Toggle Local Audio (Called by Blazor button)
     */
    async toggleAudio() {
        if (!this.activeRoom) return false;
        const local = this.activeRoom.localParticipant;
        const newState = !local.isMicrophoneEnabled;
        try {
            if (newState) {
                await this._publishMicrophoneTrack(local);
            } else {
                await local.setMicrophoneEnabled(false);
            }
        } catch (e) {
            this._notifyMediaError('microphone', e);
            throw e;
        }

        const info = this.participants.get(local.identity);
        if (info && info.micEl) {
            info.micEl.className = `lk-mic-status ${newState ? 'unmuted' : 'muted'}`;
            info.micEl.innerHTML = newState ? '🎤' : '🔇';
        }

        this._updateLocalState();
        this._notifyParticipants();
        return newState;
    },

    /**
     * Toggle Local Video (Called by Blazor button)
     */
    async toggleVideo() {
        if (!this.activeRoom) return false;
        const local = this.activeRoom.localParticipant;
        const newState = !local.isCameraEnabled;
        await local.setCameraEnabled(newState);

        const info = this.participants.get(local.identity);
        if (info) {
            if (newState) {
                if (info.videoEl) info.videoEl.style.display = 'block';
                if (info.avatarEl) info.avatarEl.style.display = 'none';
            } else {
                if (info.videoEl) info.videoEl.style.display = 'none';
                if (info.avatarEl) info.avatarEl.style.display = 'flex';
            }
        }

        this._updateLocalState();
        this._notifyParticipants();
        return newState;
    },

    /**
     * Disconnect and clean up
     */
    async leaveRoom() {
        if (this.activeRoom) {
            try {
                await this.activeRoom.disconnect();
            } catch (e) {
                console.warn('[LiveKitBridge] Disconnect error:', e);
            }
            this.activeRoom = null;
        }

        if (this.gridContainer) {
            this.gridContainer.innerHTML = '';
        }
        if (this.audioContainer) {
            this.audioContainer.innerHTML = '';
        }
        this.participants.clear();
        console.log('[LiveKitBridge] Left room & cleaned up DOM.');
    },

    /**
     * Ask the authenticated server for the LiveKit connection details.
     * Browser cookies are sent automatically, but token values never enter
     * the Blazor component or JavaScript application state.
     */
    async fetchConnectionDetails(roomName) {
        const params = new URLSearchParams({
            roomName: roomName || ''
        });
        const request = () => fetch(`/api/connection-details?${params.toString()}`, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json' }
        });
        let response = await request();

        // If the short-lived access token expired, rotate the stored refresh token
        // and retry once. A revoked/deactivated account still fails closed.
        if (response.status === 401) {
            const refreshResponse = await fetch('/api/auth/refresh', {
                method: 'POST',
                credentials: 'same-origin'
            });
            if (refreshResponse.ok) {
                response = await request();
            }
        }

        if (!response.ok) {
            let message = `Unable to get connection details (${response.status}).`;
            try {
                const body = await response.json();
                if (body && body.error) message = body.error;
            } catch (_) {
                // Keep the status-based message when the response is not JSON.
            }
            throw new Error(message);
        }

        return await response.json();
    },

    /**
     * Copy text to clipboard helper
     */
    async copyToClipboard(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            console.error('Failed to copy to clipboard', e);
            return false;
        }
    }
};
