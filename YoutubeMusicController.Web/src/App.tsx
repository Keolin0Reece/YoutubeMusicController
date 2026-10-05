import { useEffect, useState, type FormEvent } from "react";
import "./App.css";

const API_URL =
  (import.meta.env.VITE_API_URL ?? "http://localhost:5161").replace(
    /\/$/,
    ""
  );

interface NowPlaying {
  title: string | null;
  alternativeTitle: string | null;
  artist: string | null;
  artistUrl: string | null;
  views: number;
  uploadDate: string | null;
  imageSrc: string | null;
  isPaused: boolean;
  songDuration: number;
  elapsedSeconds: number;
  url: string | null;
  album: string | null;
  videoId: string | null;
  playlistId: string | null;
  mediaType: string | null;
  tags: string[];
}

interface SearchResult {
  title: string;
  artists: string[];
  album: string | null;
  duration: string | null;
  imageSrc: string | null;
  videoId: string;
  mediaType: string;
}

interface SearchResponse {
  results: SearchResult[];
  continuation: string | null;
}

interface QueueNotice {
  type: "success" | "error";
  message: string;
}

interface QueueItem {
  index: number;
  title: string;
  artists: string[];
  album: string | null;
  duration: string | null;
  imageSrc: string | null;
  videoId: string;
  isSelected: boolean;
  canReorder: boolean;
}

interface QueueResponse {
  items: QueueItem[];
  selectedIndex: number | null;
  autoPlaying: boolean;
  hasMore: boolean;
}

interface RealtimeMessage {
  type: string;
  song?: NowPlaying;
  isPlaying?: boolean;
  position?: number;
}

interface TwitchConnectionStatus {
  configured: boolean;
  authorized: boolean;
  connected: boolean;
  state: string;
  accountName: string | null;
  error: string | null;
}

interface TwitchChatMessage {
  id: string;
  username: string;
  displayName: string;
  message: string;
  receivedAt: string;
}

function App() {
  const [connected, setConnected] = useState(false);
  const [nowPlaying, setNowPlaying] = useState<NowPlaying | null>(null);
  const [message, setMessage] = useState("Ready");
  const [loading, setLoading] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState<
    SearchResult[]
  >([]);
  const [searchLoading, setSearchLoading] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(
    null
  );
  const [hasSearched, setHasSearched] = useState(false);
  const [queueingVideoId, setQueueingVideoId] = useState<
    string | null
  >(null);
  const [queueNotice, setQueueNotice] = useState<QueueNotice | null>(
    null
  );
  const [queue, setQueue] = useState<QueueResponse | null>(null);
  const [queueLoading, setQueueLoading] = useState(false);
  const [queueMutation, setQueueMutation] = useState<string | null>(
    null
  );
  const [queueError, setQueueError] = useState<string | null>(null);
  const [queueManagementNotice, setQueueManagementNotice] =
    useState<QueueNotice | null>(null);
  const [confirmingClear, setConfirmingClear] = useState(false);
  const [realtimeConnected, setRealtimeConnected] = useState(false);
  const [twitchStatus, setTwitchStatus] =
    useState<TwitchConnectionStatus | null>(null);
  const [twitchMessages, setTwitchMessages] = useState<
    TwitchChatMessage[]
  >([]);
  const [twitchLoading, setTwitchLoading] = useState(false);

  const fetchNowPlaying = async () => {
    try {
      const response = await fetch(
        `${API_URL}/api/ytmd/now-playing`
      );

      if (!response.ok) {
        setNowPlaying(null);
        return;
      }

      const data: NowPlaying = await response.json();

      setNowPlaying(data);
      setConnected(true);
    } catch {
      setNowPlaying(null);
      setConnected(false);
    }
  };

  const fetchQueue = async (showLoading = false) => {
    try {
      if (showLoading) {
        setQueueLoading(true);
      }

      setQueueError(null);

      const response = await fetch(`${API_URL}/api/ytmd/queue`);

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data: QueueResponse = await response.json();
      setQueue(data);
      setConnected(true);
    } catch (error) {
      console.error(error);
      setQueueError("Unable to load the YouTube Music queue.");
    } finally {
      if (showLoading) {
        setQueueLoading(false);
      }
    }
  };

  const togglePlayPause = async () => {
    try {
      setLoading(true);
      setMessage(
        nowPlaying?.isPaused
          ? "Playing..."
          : "Pausing..."
      );

      const response = await fetch(
        `${API_URL}/api/ytmd/play-pause`,
        {
          method: "POST",
        }
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      await fetchNowPlaying();

      setMessage("Playback updated");
    } catch (error) {
      console.error(error);
      setMessage("Unable to control YouTube Music");
    } finally {
      setLoading(false);
    }
  };

  const next = async () => {
    try {
      setLoading(true);
      setMessage("Skipping...");

      const response = await fetch(
        `${API_URL}/api/ytmd/next`,
        {
          method: "POST",
        }
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      await fetchNowPlaying();

      setMessage("Playing next track");
    } catch (error) {
      console.error(error);
      setMessage("Unable to skip track");
    } finally {
      setLoading(false);
    }
  };

  const previous = async () => {
    try {
      setLoading(true);
      setMessage("Going back...");

      const response = await fetch(
        `${API_URL}/api/ytmd/previous`,
        {
          method: "POST",
        }
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      await fetchNowPlaying();

      setMessage("Playing previous track");
    } catch (error) {
      console.error(error);
      setMessage("Unable to go to previous track");
    } finally {
      setLoading(false);
    }
  };

  const search = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    const query = searchQuery.trim();

    if (!query) {
      return;
    }

    try {
      setSearchLoading(true);
      setSearchError(null);
      setQueueNotice(null);
      setHasSearched(true);

      const response = await fetch(`${API_URL}/api/ytmd/search`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ query }),
      });

      if (!response.ok) {
        let detail = "Search is unavailable right now.";

        try {
          const problem: { detail?: string } = await response.json();
          detail = problem.detail ?? detail;
        } catch {
          // Keep the friendly fallback when the server has no JSON body.
        }

        throw new Error(detail);
      }

      const data: SearchResponse = await response.json();
      setSearchResults(data.results);
    } catch (error) {
      console.error(error);
      setSearchResults([]);
      setSearchError(
        error instanceof Error
          ? error.message
          : "Search is unavailable right now."
      );
    } finally {
      setSearchLoading(false);
    }
  };

  const addToQueue = async (result: SearchResult) => {
    try {
      setQueueingVideoId(result.videoId);
      setQueueNotice(null);

      const response = await fetch(`${API_URL}/api/ytmd/queue`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ videoId: result.videoId }),
      });

      if (!response.ok) {
        let detail = "Unable to add this track to the queue.";

        try {
          const problem: { detail?: string } = await response.json();
          detail = problem.detail ?? detail;
        } catch {
          // Keep the friendly fallback when the server has no JSON body.
        }

        throw new Error(detail);
      }

      setQueueNotice({
        type: "success",
        message: `Added “${result.title}” to the end of the queue.`,
      });
      await fetchQueue();
    } catch (error) {
      console.error(error);
      setQueueNotice({
        type: "error",
        message:
          error instanceof Error
            ? error.message
            : "Unable to add this track to the queue.",
      });
    } finally {
      setQueueingVideoId(null);
    }
  };

  const removeQueueItem = async (item: QueueItem) => {
    try {
      setQueueMutation(`remove-${item.index}`);
      setQueueManagementNotice(null);

      const response = await fetch(
        `${API_URL}/api/ytmd/queue/${item.index}`,
        { method: "DELETE" }
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      setQueueManagementNotice({
        type: "success",
        message: `Removed “${item.title}” from the queue.`,
      });
      await fetchQueue();
    } catch (error) {
      console.error(error);
      setQueueManagementNotice({
        type: "error",
        message: "Unable to remove that queue item.",
      });
    } finally {
      setQueueMutation(null);
    }
  };

  const moveQueueItem = async (item: QueueItem, toIndex: number) => {
    try {
      setQueueMutation(`move-${item.index}`);
      setQueueManagementNotice(null);

      const response = await fetch(
        `${API_URL}/api/ytmd/queue/${item.index}`,
        {
          method: "PATCH",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ toIndex }),
        }
      );

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      await fetchQueue();
    } catch (error) {
      console.error(error);
      setQueueManagementNotice({
        type: "error",
        message: "Unable to reorder the queue.",
      });
    } finally {
      setQueueMutation(null);
    }
  };

  const clearQueue = async () => {
    try {
      setQueueMutation("clear");
      setQueueManagementNotice(null);

      const response = await fetch(`${API_URL}/api/ytmd/queue`, {
        method: "DELETE",
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      setConfirmingClear(false);
      setQueueManagementNotice({
        type: "success",
        message: "The queue was cleared.",
      });
      await fetchQueue();
    } catch (error) {
      console.error(error);
      setQueueManagementNotice({
        type: "error",
        message: "Unable to clear the queue.",
      });
    } finally {
      setQueueMutation(null);
    }
  };

  const fetchTwitchState = async () => {
    try {
      const [statusResponse, messagesResponse] = await Promise.all([
        fetch(`${API_URL}/api/twitch/status`),
        fetch(`${API_URL}/api/twitch/messages`),
      ]);

      if (!statusResponse.ok || !messagesResponse.ok) {
        throw new Error("Unable to load Twitch state.");
      }

      const [status, messages]: [
        TwitchConnectionStatus,
        TwitchChatMessage[],
      ] = await Promise.all([
        statusResponse.json(),
        messagesResponse.json(),
      ]);

      setTwitchStatus(status);
      setTwitchMessages(messages);
    } catch (error) {
      console.error(error);
    }
  };

  const connectTwitch = () => {
    window.location.assign(`${API_URL}/api/twitch/connect`);
  };

  const disconnectTwitch = async () => {
    try {
      setTwitchLoading(true);
      const response = await fetch(`${API_URL}/api/twitch/disconnect`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      await fetchTwitchState();
    } catch (error) {
      console.error(error);
    } finally {
      setTwitchLoading(false);
    }
  };

  useEffect(() => {
    const initialRefresh = setTimeout(() => {
      fetchNowPlaying();
      fetchQueue(true);
    }, 0);

    const interval = setInterval(() => {
      fetchNowPlaying();
      fetchQueue();
    }, 30000);

    return () => {
      clearTimeout(initialRefresh);
      clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    const initialRefresh = setTimeout(fetchTwitchState, 0);
    const interval = setInterval(fetchTwitchState, 3000);

    return () => {
      clearTimeout(initialRefresh);
      clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    let socket: WebSocket | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let reconnectAttempt = 0;
    let stopped = false;

    const connect = () => {
      const socketUrl = `${API_URL.replace(/^http/, "ws")}/api/ytmd/realtime`;
      socket = new WebSocket(socketUrl);

      socket.onopen = () => {
        reconnectAttempt = 0;
        setRealtimeConnected(true);
        setConnected(true);
      };

      socket.onmessage = (event) => {
        try {
          const update: RealtimeMessage = JSON.parse(event.data);

          if (
            (update.type === "PLAYER_INFO" ||
              update.type === "VIDEO_CHANGED") &&
            update.song
          ) {
            setNowPlaying({
              ...update.song,
              isPaused:
                update.isPlaying === undefined
                  ? update.song.isPaused
                  : !update.isPlaying,
              elapsedSeconds:
                update.position ?? update.song.elapsedSeconds,
            });
            setConnected(true);

            if (update.type === "VIDEO_CHANGED") {
              fetchQueue();
            }
          } else if (update.type === "PLAYER_STATE_CHANGED") {
            setNowPlaying((current) =>
              current
                ? {
                    ...current,
                    isPaused:
                      update.isPlaying === undefined
                        ? current.isPaused
                        : !update.isPlaying,
                    elapsedSeconds:
                      update.position ?? current.elapsedSeconds,
                  }
                : current
            );
          } else if (
            update.type === "POSITION_CHANGED" &&
            update.position !== undefined
          ) {
            setNowPlaying((current) =>
              current
                ? { ...current, elapsedSeconds: update.position! }
                : current
            );
          }
        } catch (error) {
          console.error("Unable to read realtime update", error);
        }
      };

      socket.onerror = () => {
        socket?.close();
      };

      socket.onclose = () => {
        setRealtimeConnected(false);

        if (!stopped) {
          const delay = Math.min(1000 * 2 ** reconnectAttempt, 30000);
          reconnectAttempt++;
          reconnectTimer = setTimeout(connect, delay);
        }
      };
    };

    connect();

    return () => {
      stopped = true;

      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
      }

      socket?.close();
    };
  }, []);

  const formatTime = (seconds: number) => {
    const minutes = Math.floor(seconds / 60);
    const remainingSeconds = Math.floor(seconds % 60);

    return `${minutes}:${remainingSeconds
      .toString()
      .padStart(2, "0")}`;
  };

  return (
    <div className="app">
      <main className="player">
        <header className="header">
          <div>
            <p className="eyebrow">LOCAL CONTROLLER</p>
            <h1>YouTube Music</h1>
          </div>

          <div
            className={`connection ${
              connected
                ? "connected"
                : "disconnected"
            }`}
          >
            <span className="connection-dot" />

            {realtimeConnected
              ? "Live"
              : connected
                ? "Connected"
                : "Disconnected"}
          </div>
        </header>

        <section className="now-playing">
          <div className="album-art">
            {nowPlaying?.imageSrc ? (
              <img
                src={nowPlaying.imageSrc}
                alt={nowPlaying.title ?? "Album artwork"}
              />
            ) : (
              <span>♫</span>
            )}
          </div>

          <div className="track-info">
            <span className="track-label">
              NOW PLAYING
            </span>

            <h2>
              {nowPlaying?.title ??
                "No song playing"}
            </h2>

            <p>
              {nowPlaying?.artist ??
                "Waiting for track information..."}
            </p>

            {nowPlaying?.album && (
              <span className="album-name">
                {nowPlaying.album}
              </span>
            )}
          </div>
        </section>

        {nowPlaying && (
          <section className="progress">
            <div className="progress-bar">
              <div
                className="progress-fill"
                style={{
                  width: `${
                    nowPlaying.songDuration > 0
                      ? (nowPlaying.elapsedSeconds /
                          nowPlaying.songDuration) *
                        100
                      : 0
                  }%`,
                }}
              />
            </div>

            <div className="progress-times">
              <span>
                {formatTime(
                  nowPlaying.elapsedSeconds
                )}
              </span>

              <span>
                {formatTime(
                  nowPlaying.songDuration
                )}
              </span>
            </div>
          </section>
        )}

        <section className="controls">
          <button
            className="control-button secondary"
            onClick={previous}
            disabled={!connected || loading}
            aria-label="Previous"
          >
            ‹
          </button>

          <button
            className="control-button primary"
            onClick={togglePlayPause}
            disabled={!connected || loading}
            aria-label="Play or pause"
          >
            {loading
              ? "..."
              : nowPlaying?.isPaused
                ? "▶"
                : "Ⅱ"}
          </button>

          <button
            className="control-button secondary"
            onClick={next}
            disabled={!connected || loading}
            aria-label="Next"
          >
            ›
          </button>
        </section>

        <div className="status-message">
          {message}
        </div>

        <section className="twitch-section">
          <div className="twitch-heading">
            <div>
              <span className="twitch-icon" aria-hidden="true">
                ◈
              </span>

              <div>
                <strong>Twitch Chat</strong>
                <p>
                  Try !song, !nowplaying, or !queue in chat.
                </p>
              </div>
            </div>

            <span
              className={`twitch-state ${
                twitchStatus?.connected ? "connected" : ""
              }`}
            >
              {twitchStatus?.state ?? "Checking…"}
            </span>
          </div>

          {twitchStatus && !twitchStatus.configured && (
            <div className="twitch-setup">
              Add the Twitch client ID and secret to local .NET
              user-secrets before connecting.
            </div>
          )}

          {twitchStatus?.error && (
            <div className="twitch-error" role="alert">
              {twitchStatus.error}
            </div>
          )}

          <div className="twitch-actions">
            <div>
              {twitchStatus?.accountName
                ? `Signed in as ${twitchStatus.accountName}`
                : "No Twitch account connected"}
            </div>

            {twitchStatus?.authorized ? (
              <button
                type="button"
                className="twitch-disconnect"
                onClick={disconnectTwitch}
                disabled={twitchLoading}
              >
                {twitchLoading ? "Disconnecting…" : "Disconnect"}
              </button>
            ) : (
              <button
                type="button"
                className="twitch-connect"
                onClick={connectTwitch}
                disabled={!twitchStatus?.configured}
              >
                Connect Twitch
              </button>
            )}
          </div>

          <div className="twitch-messages" aria-live="polite">
            <div className="twitch-messages-title">
              <span>Recent chat messages</span>
              <span>{twitchMessages.length}/20</span>
            </div>

            {twitchMessages.length === 0 ? (
              <p className="twitch-empty">
                Connect Twitch, then send a message in your channel.
              </p>
            ) : (
              <ul>
                {twitchMessages.map((chatMessage) => (
                  <li key={chatMessage.id}>
                    <time>
                      {new Date(chatMessage.receivedAt).toLocaleTimeString(
                        [],
                        { hour: "2-digit", minute: "2-digit" }
                      )}
                    </time>
                    <strong>{chatMessage.displayName}</strong>
                    <span>{chatMessage.message}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </section>

        <section className="search-section">
          <div className="search-heading">
            <div>
              <span className="feature-icon" aria-hidden="true">
                ⌕
              </span>

              <div>
                <strong>Search YouTube Music</strong>
                <p>Find a track before adding it to your queue.</p>
              </div>
            </div>

            <span className="badge">SEARCH</span>
          </div>

          <form className="search-form" onSubmit={search}>
            <label className="visually-hidden" htmlFor="music-search">
              Song, artist, or album
            </label>

            <input
              id="music-search"
              type="search"
              value={searchQuery}
              onChange={(event) => setSearchQuery(event.target.value)}
              placeholder="Song, artist, or album"
              autoComplete="off"
            />

            <button
              type="submit"
              disabled={searchLoading || !searchQuery.trim()}
            >
              {searchLoading ? "Searching…" : "Search"}
            </button>
          </form>

          {searchError && (
            <div className="search-state error" role="alert">
              {searchError}
            </div>
          )}

          {queueNotice && (
            <div
              className={`queue-notice ${queueNotice.type}`}
              role={queueNotice.type === "error" ? "alert" : "status"}
            >
              {queueNotice.message}
            </div>
          )}

          {!searchLoading &&
            !searchError &&
            hasSearched &&
            searchResults.length === 0 && (
              <div className="search-state">
                No playable tracks found. Try a different search.
              </div>
            )}

          {searchResults.length > 0 && (
            <div className="search-results" aria-live="polite">
              <div className="results-summary">
                <span>
                  {searchResults.length}{" "}
                  {searchResults.length === 1 ? "track" : "tracks"}
                </span>
                <span>Results for “{searchQuery.trim()}”</span>
              </div>

              <ul>
                {searchResults.map((result) => (
                  <li key={result.videoId} className="search-result">
                    <div className="result-art">
                      {result.imageSrc ? (
                        <img src={result.imageSrc} alt="" />
                      ) : (
                        <span aria-hidden="true">♫</span>
                      )}
                    </div>

                    <div className="result-info">
                      <strong title={result.title}>{result.title}</strong>
                      <span>
                        {result.artists.length > 0
                          ? result.artists.join(", ")
                          : "Unknown artist"}
                      </span>
                      {result.album && <small>{result.album}</small>}
                    </div>

                    <div className="result-actions">
                      <div className="result-meta">
                        <span>{result.mediaType}</span>
                        {result.duration && <time>{result.duration}</time>}
                      </div>

                      <button
                        type="button"
                        className="queue-button"
                        onClick={() => addToQueue(result)}
                        disabled={
                          queueingVideoId !== null
                        }
                        aria-label={`Add ${result.title} to queue`}
                      >
                        {queueingVideoId === result.videoId
                          ? "Adding…"
                          : "+ Queue"}
                      </button>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </section>

        <section className="queue-section">
          <div className="queue-heading">
            <div>
              <span className="feature-icon" aria-hidden="true">
                ≡
              </span>

              <div>
                <strong>Current Queue</strong>
                <p>
                  {realtimeConnected
                    ? "Live playback updates are connected."
                    : "Using recovery polling while live updates reconnect."}
                </p>
              </div>
            </div>

            <div className="queue-header-actions">
              <button
                type="button"
                onClick={() => fetchQueue(true)}
                disabled={queueLoading || queueMutation !== null}
              >
                Refresh
              </button>
              <button
                type="button"
                className="danger"
                onClick={() => setConfirmingClear(true)}
                disabled={
                  !queue?.items.length ||
                  queueLoading ||
                  queueMutation !== null
                }
              >
                Clear
              </button>
            </div>
          </div>

          {confirmingClear && (
            <div className="clear-confirmation" role="alertdialog">
              <span>Clear every item from the playback queue?</span>
              <div>
                <button
                  type="button"
                  onClick={() => setConfirmingClear(false)}
                  disabled={queueMutation !== null}
                >
                  Cancel
                </button>
                <button
                  type="button"
                  className="confirm-danger"
                  onClick={clearQueue}
                  disabled={queueMutation !== null}
                >
                  {queueMutation === "clear" ? "Clearing…" : "Clear queue"}
                </button>
              </div>
            </div>
          )}

          {queueManagementNotice && (
            <div
              className={`queue-notice ${queueManagementNotice.type}`}
              role={
                queueManagementNotice.type === "error" ? "alert" : "status"
              }
            >
              {queueManagementNotice.message}
            </div>
          )}

          {queueError && (
            <div className="search-state error" role="alert">
              {queueError}
            </div>
          )}

          {queueLoading && !queue && (
            <div className="search-state">Loading queue…</div>
          )}

          {!queueLoading && queue && queue.items.length === 0 && (
            <div className="search-state">The playback queue is empty.</div>
          )}

          {queue && queue.items.length > 0 && (
            <div className="queue-content">
              <div className="results-summary">
                <span>
                  {queue.items.length}{" "}
                  {queue.items.length === 1 ? "visible item" : "visible items"}
                </span>
                <span>{queue.autoPlaying ? "Autoplay on" : "Autoplay off"}</span>
              </div>

              <ol className="queue-list" aria-live="polite">
                {queue.items.map((item, visibleIndex) => (
                  <li
                    key={`${item.videoId}-${item.index}`}
                    className={`queue-item ${item.isSelected ? "selected" : ""}`}
                  >
                    <span className="queue-position">
                      {item.isSelected ? "▶" : item.index + 1}
                    </span>

                    <div className="queue-art">
                      {item.imageSrc ? (
                        <img src={item.imageSrc} alt="" />
                      ) : (
                        <span aria-hidden="true">♫</span>
                      )}
                    </div>

                    <div className="queue-item-info">
                      <strong title={item.title}>{item.title}</strong>
                      <span>
                        {item.artists.length > 0
                          ? item.artists.join(", ")
                          : "Unknown artist"}
                      </span>
                      {item.album && <small>{item.album}</small>}
                    </div>

                    {item.duration && (
                      <time className="queue-duration">{item.duration}</time>
                    )}

                    <div className="queue-item-actions">
                      <button
                        type="button"
                        onClick={() => moveQueueItem(item, item.index - 1)}
                        disabled={
                          queueMutation !== null ||
                          !item.canReorder ||
                          item.index === 0
                        }
                        aria-label={`Move ${item.title} up`}
                      >
                        ↑
                      </button>
                      <button
                        type="button"
                        onClick={() => moveQueueItem(item, item.index + 1)}
                        disabled={
                          queueMutation !== null ||
                          !item.canReorder ||
                          (visibleIndex === queue.items.length - 1 &&
                            !queue.hasMore)
                        }
                        aria-label={`Move ${item.title} down`}
                      >
                        ↓
                      </button>
                      <button
                        type="button"
                        className="remove"
                        onClick={() => removeQueueItem(item)}
                        disabled={queueMutation !== null || item.isSelected}
                        aria-label={`Remove ${item.title} from queue`}
                      >
                        ×
                      </button>
                    </div>
                  </li>
                ))}
              </ol>

              {queue.hasMore && (
                <p className="queue-more-note">
                  YouTube Music has more queued items beyond this visible segment.
                </p>
              )}
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

export default App;
