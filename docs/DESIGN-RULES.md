# What we look for: design rules for speed, lifecycle and the way around

Nixie for desktop and Nixie for TV both follow these rules. Every change is checked against them, and so is every bug
hunt; the hunt checks them as class 12. Each rule comes from a real bug, given in brackets.

1. **The first screen waits only for what it shows.**
   - Saved data comes first and the rest fills in as it arrives.
   - Background work (indexes, launcher rows, outside lists) starts after the first screen is drawn.
   - One slow or failed part never holds the others.

   [TV: Home took about a minute on a 25,800-film server, because it waited for every row and for start-up work.]

2. **Time new paths against a real Jellyfin at scale.**
   - Use the QA servers ([QA-SERVERS.md](QA-SERVERS.md)) or a throwaway instance with about 25,000 items, never a
     user's own server.
   - A fake server answers at once whatever is asked, so its numbers aren't evidence of speed.

   [TV: a library's first row took 0.66 s against the fake and about 10 s on a real server.]

3. **No whole-library work on an interaction path.**
   - A count, an index, a collapse or a full read over the library happens in the background, or comes from what's
     saved, never between a key press and its answer.
   - An A–Z jump asks for its offset, not for the whole index.

   [TV: the first A–Z jump read the whole index, 26 pages.]

4. **Nothing destructive runs on "not loaded yet".**
   - Missing data is not a decision: a cleanup, delete or reset waits until the thing it judges is known.

   [TV: Continue watching on the launcher was emptied at every start, because "no profile yet" was read as "another
   profile".]

5. **Sleep, standby, minimise and offline release what they hold.**
   - Players, sockets and timers let go when nobody is watching, retries back off to minutes, and everything comes back
     where it was on return.

   [TV: a paused player was held all night in standby, while the live-updates socket retried every minute.]

6. **Device facts come from the real source.**
   - The display's physical mode, not the UI's size; the decoder's real profiles; the files the package really ships.
   - Test 4K, HDR and Dolby Vision content.

   [TV: playback logged a 1920x1080 limit on a 3840x2160 TV. Desktop 1.6.0: skip snapping relied on an mpv the
   download didn't include.]

7. **Views open at their final size, with no jump.**
   - Time to first frame is measured, for playback and for trailers.

   [TV: a trailer opened small, jumped to full screen, and took about 25 s to show its first frame.]

8. **Every page has a way up to its parent,** whatever opened it.

   [Desktop 1.6.1: an episode opened from Continue watching, search or Up next had no link to its series or season.]

9. **A switch turned off stops in-flight work too.**
   - Turning something off cancels what it started, and nothing it began lands afterwards.

   [Desktop 1.7.0: lists kept being requested after their source was switched off mid-refresh.]

10. **Detailed logs record the durations of the paths users wait on.**
    - Start-up, the first screen, each row, a library's first page, search, playback and the first frame of a trailer.
    - Durations only: no titles or names.
    - The next check on a real device then gives numbers without a debugger.

    [TV: the log showed when Home and a library opened, but not how long their parts took.]

Where the rules live:
- the visual design rules: [design/DESIGN.md](design/DESIGN.md);
- the release steps: [RELEASE.md](RELEASE.md).
