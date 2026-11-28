# Collections 2: the category catalogue and its census

Nixie 1.9 (Collections 2) ships Netflix-style categories as data:

- `ui\FluentFin.Core\SmartCollections\Data\micro-genres.json`: the catalogue (shape in `briefs\190-plan.md`);
- `ui\FluentFin.Core\SmartCollections\Data\keywords-matchable.json`: the TMDb keywords a rule may match beyond the
  allowlist (`Keywords.cs`).

Both are embedded in Core and read by `MicroGenres` (loader and validator), matched by `MicroGenreMatcher`.

| File | What |
|---|---|
| `draft.json` | Every drafted category (446), in the shipped layout. The authoring source. |
| `kept.txt` | The ids kept for the shipped catalogue (299). The shipped file is `draft.json` cut by this list. |
| `text-cases.json` | Sentences each shows' summary word list must match, and must not (the text review's false matches among them). |
| `patches\` | Batches of rule changes applied to the draft (`--patch`), each change with why. |
| `Census\` | The census tool (a .NET console). |
| `Census.Tests\` | Its tests, and the shipped files' lint (no problem, the cut is exact, the list has the loader's shape). |

## The census tool

Counts only on the console: never a title, an id or a person. Run it from the repository root; the catalogue and the
list default to the repository's `Data` folder.

```
dotnet run --project tools\collections\Census -- --check
dotnet run --project tools\collections\Census -- --index <a saved index .json> [--json <out.json>]
dotnet run --project tools\collections\Census -- --index <a saved index .json> --vocab 150
dotnet run --project tools\collections\Census -- --catalogue tools\collections\draft.json --format ui\FluentFin.Core\SmartCollections\Data\micro-genres.json --keep tools\collections\kept.txt
dotnet run --project tools\collections\Census -- --catalogue tools\collections\draft.json --patch tools\collections\patches\text-review.json --format tools\collections\draft.json
```

- `--check`: the lint. It covers:
  - names: at most 50 characters, `NixieTitles.Check`, no filler or praise, a mood word only with a tone keyword or genre
    that backs it, a region word only with a country or network, decades and lengths that match the rule;
  - rules: known genres per kind, every keyword allowlisted or matchable, no keywords, countries or critics' ratings for
    shows, summary words for shows only;
  - parents: the keyword builder's ids (`keyword:neonoir`);
  - the matchable list: TMDb's junk, spoilers, and sex and harm words are refused.
- `--index`: each category's pool on a saved library index, read with the app's own `IndexEntry` and `CanonicalTitles`
  (copies once). The table shows:
  - all and unwatched;
  - the verdict against the floor (8 unwatched) and its kind's ceiling (1 % of that kind, at least 30);
  - the closest category of its family (Jaccard);
  - the overlap with its parent keyword collection;
  - for shows, how many shows with played episodes it holds.

  A rule on a keyword the index doesn't keep, or on summary words, is "unknown", with what it surely holds and what it
  may hold. What the index kept follows its saved revision: 3 and before, the allowlist and the six seasonal keywords; 4
  (1.9) and later, every matchable keyword.
- `--patch`: apply a batch of rule or reason-line changes (never names) before `--format`.

## Summary word lists (shows, `textAny`)

The app's summaries pass matches whole words, case-folded, with no stems ("vampire" isn't in "vampires"), and a phrase only
as those words in a row inside one sentence. `textMin` (2) asks for that many different entries of the list.

- Prefer phrases to single words that mean other things: "small town", not "town"; "the bar exam" must not be a bar.
- Gate every list with genres (`genresAny`), so "invasion" in a war drama can't make it an alien show.
- Use `textMin: 2` for lists of common words ("family", "boss", "police").
- Add sentences to `text-cases.json` for every list: at least one it must match and one it must not. The tests run them all.

## Show genres on TMDb and TheTVDB servers

A server fed by TheTVDB keeps show genres such as "Action", "Science Fiction", "Anime" and "Mini-Series" (the owner's:
1,241, 724, 1,281 and 323 shows). A TMDb-fed one keeps "Action & Adventure", "Sci-Fi & Fantasy", "War & Politics", "Kids"
and "Talk". A shows rule that needs one of TheTVDB's with a TMDb counterpart accepts both (`genresAny`), and the lint says
so. TheTVDB's Anime, Mini-Series, Horror, Thriller, Romance, History, Sport, Food, Game Show and Musical have no TMDb show
genre: those categories stay empty on a TMDb-fed server.
- `--vocab`: the index's genres, countries, studios or networks, ratings, decades and lengths per kind, with counts
  (public category names only).
- `--examples`, `--names`: up to 5 titles a known pool, written **to a local file only**, for the owner's review of the
  names. They hold titles of a library: never commit their output, never print it.

Open a saved index only as a copy (the tool opens it with `FileShare.ReadWrite` and never writes it), and delete the copy
after.

## Adding or changing a category

1. Edit `draft.json` (one category per two lines). Add any new keyword to `keywords-matchable.json`, in a group whose
   reason fits.
   - Changing the matchable list means a new index revision (every title read again behind the saved copy), so add
     keywords in batches.
2. `--check` must say 0 problems; run the census on a big library's index; keep the id in `kept.txt` if its pool fits.
3. Rewrite the shipped file with `--format … --keep kept.txt`.
4. `dotnet test tools\collections\Census.Tests`. The owner reviews new names before they ship.
