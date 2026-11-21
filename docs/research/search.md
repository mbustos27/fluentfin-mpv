# Search: how Jellyfin matches, and what Nixie asks (Nixie 1.5)

The owner (2026-11-19): "search is pretty bad". On their server (Jellyfin 12.4.0), "it's always sunny" gave
"Movies (100 of 295)": a fixed Movies group first, full of unrelated films, with the show out of view. This note is
item 1 of the 1.5 search stream: why, measured on the local QA servers (10.11.11 and 12.4.0, `docs\QA-SERVERS.md`)
and read in the server source (tags `v10.11.11` and `v12.4` of github.com/jellyfin/jellyfin). The owner's server was
never queried.

## How each server matches

**10.11.11:** `/Items?searchTerm=` and `/Search/Hints` both filter in the database
(`Jellyfin.Server.Implementations/Item/BaseItemRepository.cs`, `TranslateQuery`, around line 1779):
- `CleanName` (the name without accents, lower-cased; punctuation **kept**) contains the term, or `OriginalTitle`
  lower-cased contains it. One substring: the words in the order typed, nothing else.
- `%`, `_`, `[`, `]` or `^` in the term make it a SQL `LIKE` pattern instead: "%" matches everything.
- Order (`ApplyOrder`, around line 1595): `OrderMapper.MapSearchRelevanceOrder` (exact name, then the name starting
  with the term and a space, then starting with it, then anything else), then the sort name.

**12.4:** `/Items?searchTerm=` no longer filters itself. `Jellyfin.Api/Controllers/ItemsController.cs` (around line 353)
asks `ISearchManager` (`Emby.Server.Implementations/Library/Search/SearchManager.cs`) for candidate ids and scores,
then loads those items and sorts them by score:
- **Search providers:** plugins can add *external* providers. When any external provider answers, its candidates are
  used and the built-in one is ignored (`GetSearchResultsAsync`).
- **The built-in provider** (`SqlSearchProvider.cs`): `CleanName` contains the cleaned term, or `OriginalTitle LIKE
  %term%`, or `SortName LIKE %sort-shaped term%`. In 12.x `GetCleanValue` (`src/Jellyfin.Extensions/StringExtensions.cs`)
  turns punctuation into spaces, so "spy-x-family" and "the-matrix" now match. Still one substring in typed order.
- **Score:** exact 100, the name starting with the term 80, a word starting with it 75, anything else 50, then the
  item id; the controller then sorts by score, then sort name.
- **The total is capped:** the providers are asked for `limit × 3` candidates (100 with no limit), and
  `TotalRecordCount` is the number of candidates left after access filtering. It is not a count of matches.

## What the QA servers answered (2026-11-20, qa-user, `/Items` per type, limit 100)

| Query | 10.11.11 | 12.4.0 |
|---|---|---|
| matrix | The Matrix, The Matrix Reloaded | **The Matrix Reloaded, The Matrix** (a word start, 75, beats the exact title after "The", 50) |
| matrix reloaded / the matrix | found | found |
| reloaded matrix, zone twilight, night dead, steel tears | nothing | nothing |
| twilight zone 1959 (an extra word) | nothing | nothing |
| the-matrix, matrix:, spy-x-family | nothing | found |
| it's always sunny, its always sunny | nothing (no such title seeded) | nothing |
| destin d'amélie (the original title) | found | **nothing** (the accents are taken off the term, not off the stored title) |
| destin d’amélie (curly apostrophe) | nothing | nothing |
| AMÉLIE, amelie | found | found |
| % or _ | everything | everything |
| a (people) | none in `/Items` | 160 people |
| "a", people, `limit=5` / 10 / 40 / none | 0 | `TotalRecordCount` 15 / 30 / 120 / 100: three times the limit |

`/Search/Hints` matched the same way on both (the same filter in 10.11, the same providers in 12.4), with people,
genres, seasons and music videos mixed in under one shared limit, and it answers with `SearchHint` rather than
`BaseItemDto` (no watched state or progress for the cards). An empty `searchTerm` is a 400 there. On `/Items`, an empty
term is dropped (all items), an unknown parameter is dropped (200, same answer), and no token or a bad token is a 401.

## The same on a big library (2026-11-20, "QA Search": 3,035 films, 29 shows, 96 episodes, 40 people)

`tools\qa-servers\seed.ps1` step 4 (`search_seed.py`) adds titles from .nfo files with every internet fetcher off,
near-misses included ("It's Always Fair Weather", "Always", "Sunny", "It", "It's a Wonderful Life", "Little Miss
Sunshine", the show "It's Always Sunny in Philadelphia" with 13 episodes, "Sunny Days" with an episode "Always Sunny").

| Query | 10.11.11 | 12.4.0 |
|---|---|---|
| it's always sunny | the show only | the show only |
| its always sunny, it’s always sunny | **nothing** | the show (the sort name, and punctuation as spaces) |
| always sunny | 2 shows, 1 episode | the same |
| it's | 1 show, 2 films, 1 episode | the same, and a person ("Kurita Itsuki") |
| it | 100 of 263 films (the real count), 4 shows, 11 episodes, 16 people in `/Persons` | the same (263 is under the 300 cap) |
| night river, river night | nothing | nothing |
| mash | nothing | nothing (the title is "m a s h" to 12, "m\*a\*s\*h" to 10.11) |
| walle, wall-e | nothing | WALL·E |
| star wars 4 (the sort title) | nothing | Star Wars: A New Hope |
| sunny holm, holm (people) | `/Search/Hints` and `/Persons` only | `/Items`, `/Search/Hints` and `/Persons` |

- **`/Search/Hints` never found a title the per-kind `/Items` requests missed.** It matches the same way, under one
  limit. It does add people on 10.11, where `/Items` returns none, and its ids load fine through `/Items?ids=` (people
  too, on both).
- **`/Persons?searchTerm=`** finds people on both servers, as cards. `/Items?personIds=` lists a person's films and shows
  on both (143 for one cast member).
- **Each request takes 20 to 70 ms** once the server is warm (the first one 400 to 550 ms).
- So even with 3,000 titles a stock server can't produce 295 unrelated films for "it's always sunny".

## Why 295 films

- "Movies (100 of 295)" is 12.x's capped total: the providers were asked for 300 candidates (`limit=100 × 3`) and 295
  were left after the access filter. So the owner's server produced **300 or more** film candidates for "it's always
  sunny".
- The built-in provider can't do that: it needs "it s always sunny" (or "its always sunny" in the sort name) inside the
  title, and no film has it. So that server must have a plugin search provider that matches loosely (any word, or
  fuzzy). Its 300 best candidates replace the built-in answer.
- Nixie made it worse:
  - a fixed group order (Movies first);
  - the server's order inside each group;
  - every candidate shown;
  - the capped total shown as if it were a count of matches.

## The request shape Nixie uses

- **All together:** `/Items` per kind (Series, Movie, Episode, BoxSet), `/Persons` for people, and `/Search/Hints`
  (limit 60, people in, genres, studios and artists out).
  - `/Items` takes `limit=100`, no `sortBy` (the server's own relevance order decides which 100 come back) and
    `fields=OriginalTitle,SortName`.
  - The second review (2026-11-20) asked for the hints: ranking only what the kinds return can't surface a title the
    server never returned. On the stock servers the hints add only people on 10.11, but they cost one 20 to 40 ms request.
  - The hints' items that the kinds didn't bring are loaded through `/Items?ids=`, and each item counts once.
- **People are in** (the second review: people search for actors). A person opens "With <name>": their films and shows,
  newest first (`/Items?personIds=`).
- **Ranked again on the PC** (`ui\FluentFin.Core\Search\`), the same way on every server and with any plugin:
  - whole title, then the title's start, then all the words, then some of them;
  - apostrophes, punctuation and accents count as equal, any case, and "The" or "A" at the start doesn't block a start
    match;
  - the original and sort titles count too.
- **Strong matches** (whole, start, all words) show. Weaker ones wait behind "Show N more".
- **The heading counts what Nixie found,** not the server's capped total.
- **Typed order and extra words:** when the whole query finds nothing, Nixie asks again with its longest word, and the
  titles that have the other words too rank as strong. "night dead", "river night" and "its always sunny" on 10.11 find
  their titles.
- **Episodes lead to their show:** a show whose episode matches is ranked by that episode, and is fetched by id when
  the show's own name didn't match.

## Not fixed by Nixie

- An accented original title on 12.x ("destin d'amélie") can't be found: the server never returns it.
- "mash" for M\*A\*S\*H, and on 10.11 "walle" for WALL·E or "star wars 4" for a sort title: one word the server can't
  match, so there is no word left to ask again with.
- A loose plugin's weak matches are still fetched (up to 100 per type). Nixie only folds them away.
