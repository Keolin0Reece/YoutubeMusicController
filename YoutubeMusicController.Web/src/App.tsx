import { useEffect, useState, type FormEvent } from "react";
import "./App.css";

const API_URL =
  import.meta.env.VITE_API_URL ?? "http://localhost:5161";

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

  useEffect(() => {
    const initialRefresh = setTimeout(fetchNowPlaying, 0);

    const interval = setInterval(() => {
      fetchNowPlaying();
    }, 3000);

    return () => {
      clearTimeout(initialRefresh);
      clearInterval(interval);
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

            {connected
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

                    <div className="result-meta">
                      <span>{result.mediaType}</span>
                      {result.duration && <time>{result.duration}</time>}
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

export default App;
