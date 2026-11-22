# Smart collections: what the server's data supports (Nixie 1.6)

Item 1 of the 1.6 collections stream, done before any builder: per kind of "Made by Nixie" collection (the smart
collections plan, section 1.3), whether most titles carry its data, measured on the local QA servers 10.11.11 and
12.4.0 (`docs\QA-SERVERS.md`) as `qa-user`, an ordinary user, on 2026-11-21. The owner's server was never queried.

**How:** `tools\ui-harness\live-server-index.ps1` (opt-in, read-only) sends the index query Nixie's `LibraryIndex`
sends (recursive, typed, `Fields=ProviderIds,Tags,Genres,Studios,ProductionLocations,SortName,DateCreated`,
`ImageTypeLimit=1`, Primary only), the same query with a bogus `Fields` name, and the side checks below. The QA library
is small (7 films, 2 shows outside "QA Search"), so coverage is judged per title: does each title that should carry a
fact carry it? The "QA Search" libraries (NFO-only, no TMDb data) are left out of every coverage figure; they are timed
for cost only.

## The fields are really applied

| Check | 10.11.11 | 12.4.0 |
|---|---|---|
| The index query | 200; Movies 7, Shows 1, Anime 1 | 200; the same |
| The same with `Fields=NixieNoSuchField` | 200, the same titles, none with ProviderIds, Tags or Genres | the same |

So a test that only checks that the request succeeded proves nothing (the server drops what it doesn't know): Core's
`LibraryIndexTests` assert the field list the query names, and the fake server drops an unknown `Fields` name with 200.

## Per kind

| Kind (plan 1.3) | Data | 10.11.11 | 12.4.0 | Ships? |
|---|---|---|---|---|
| Franchises | `ProviderIds.TmdbCollection` | every film TMDb files in a collection has it: The Matrix and The Matrix Reloaded (2344), Night of the Living Dead (261590). The other four are in no TMDb collection (Blender films, Amélie). | the same | **Yes** (2 or more owned). The name comes from the films' names (plan 1.4): the server never sends `TmdbCollectionName`. |
| Box set merge | a box set's `ProviderIds.Tmdb` = the films' `TmdbCollection` | "QA Box Set" (made by name, no Tmdb id) | the same | **Yes**, tested on the fake server's admin box set (the QA servers are never re-seeded by this stream). |
| Decades | `ProductionYear` | 7 of 7 films | 7 of 7 | **Yes** (10 or more). |
| Directors | `People` (Director) | Movies: 98 people, 9 directors | the same | **Yes**, in a background pass of its own (cost below). |
| Actors | `People` (Actor, first 5 billed) | 69 actors | the same | **Yes**, the same pass. |
| Studios (films) | `Studios` | 7 of 7 | 7 of 7 | **Yes** (5 or more). |
| Studios and networks (shows) | `Studios` | networks only: "CBS"; "TV Tokyo, TV Aichi, …" | networks **and** companies: "CBS, Cayuga Productions"; "…, WIT STUDIO, CloverWorks, Shueisha, TOHO" | **Yes**, named "Studios and networks" for shows on every version (12 can't tell them apart). |
| Genre mixes | `Genres` | 7 of 7 films, 2 of 2 shows | the same | **Yes** (thresholds from the plan). |
| Keywords | `Tags` (TMDb keywords) | 7 of 7 films, 2 of 2 shows: "heist"-style keywords ("dystopia", "time travel", "cyberpunk", "zombie", "halloween", "based on manga") | the same | **Yes**, through the allowlist. 10.11 kept one keyword with a trailing space ("simulated reality "), 12 trims it: tags are trimmed and compared without case. |
| Highly rated, not watched | `CommunityRating` + UserData | 7 of 7 | 7 of 7 | **Yes** (7.5 or more). |
| Short films | `RunTimeTicks` (ffprobe) | 7 of 7 | 7 of 7 | **Yes**, films with a runtime only (a title with none is left out). On the QA servers every file is a 90-second test pattern, so every film reads as short there: the rule is right, the fixtures are short. |
| Unfinished series | episodes played, series `UnplayedItemCount` | a series' UserData has `UnplayedItemCount` but **no `LastPlayedDate`, and no `PlayedPercentage`** | `PlayedPercentage` yes, **no `LastPlayedDate`** | **Yes, from a second pass:** the played episodes (`filters=IsPlayed`, `sortBy=DatePlayed`), each with its `SeriesId` and `UserData.LastPlayedDate` (both versions), grouped by show. |
| Countries | `ProductionLocations` | 7 of 7 films; **0 of 2 shows** | the same | **Films only**, as planned. |
| Seasonal | Tags + Genres | "halloween" on Night of the Living Dead; Horror | the same | **Yes**, in season only (October, December). |
| (critic ratings) | `CriticRating` | 4 of 7 (Rotten Tomatoes scores from the bundled OMDb plugin) | the same | **Not used**, as planned: absent on many servers, and see `minCriticRating`. |

## Side checks

- **`minCriticRating` is ignored by a recursive query** (measured, both versions): `minCriticRating=101` left 7 of 7
  films, where `minCommunityRating=10.1` left 0. Nothing in Nixie filters by critic rating on the server.
- **People cost** (the index query, with and without `People`):

  | Library | 10.11.11 | 12.4.0 |
  |---|---|---|
  | Movies (7 films) | 13 ms → 71 ms | 16 ms → 75 ms |
  | QA Search (3,035 NFO films, two actors each; cost only) | 932 ms → 3,521 ms | 1,001 ms → 3,348 ms |

  People cost about 3.5 times the index without them, so they are read in a pass of their own after the index, paged,
  in the background, and a failed People pass leaves the other kinds in place. On the owner-sized library (about 25,800
  films, real casts of 20 to 60 people) that pass is minutes, not seconds: it runs once a day at most and never holds up
  the page.

## What ships in 1.6

Every kind in plan 1.3 has its data on both versions, with two adjustments: unfinished series read the played episodes
in a pass of their own (no series has a last-played date), and countries are films only. Critic ratings stay unused.
No kind is dropped.

## Running it

```powershell
tools\ui-harness\live-server-index.ps1                 # both servers, when running (qa.ps1 start -Version 12)
tools\ui-harness\live-server-index.ps1 -Version 12 -Out <scratch>\index   # and the per-title facts as JSON
```
