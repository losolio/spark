# Performance tests

Loads synthetic patients into Spark and runs Patient searches against it, so that the MongoDB and PostgreSQL
stores can be compared on the same data.

## What it does

`run-patients.sh` runs the whole thing for one store:

1. Starts the database in Docker, limited to 4 CPUs and 4 GB of memory.
2. Builds Spark.Web.R4 in Release and starts it on the host with the selected store.
3. Loads the data with `k6/load-patients.js`, with `VUS` concurrent clients. A pool of 100 practitioners and 20
   organizations is created once, and each of the `COUNT` patients then gets an encounter, ten observations, one or
   two conditions and a prescription: about 14 resources per patient. `CLINICAL=false` loads patients only.
4. Records the load time, the database size and the number of resources.
5. Runs each search in `k6/search-patients.js` for 30 seconds with 10 concurrent clients.

The data is deterministic (`k6/patients.js` and `k6/clinical.js`): patient number `i` always gets the same name,
identifier, birth date, measurements, diagnoses and prescription. Every store therefore gets identical data, and the
searches pick values that exist.

| Search | What it exercises | Matches |
|---|---|---|
| `identifier` | token, exact | 1 |
| `birthdate` | date, one day | a few |
| `family` | string, prefix | thousands |
| `given_birthyear` | string and date together | tens |
| `gender_count` | `_summary=count` | half of the patients |
| `observation_code` | token that matches a whole type | one per patient |
| `observation_value` | quantity with a prefix, in the canonical unit | a share of the patients |
| `observation_category_date` | token and date range together | thousands |
| `observation_patient` | chain ending in a token | 10 |
| `observation_chain` | chain ending in a string | thousands |
| `observation_performer` | chain to the practitioner who measured | a hundredth of the observations |
| `condition_code` | token on a diagnosis | a sixth of the patients |
| `condition_onset` | date range over onset dates | a share of the conditions |
| `encounter_class_date` | token and period together | a share of the encounters |
| `encounter_date` | period | a share of the encounters |
| `medication_code` | token on a prescription | a fifth of the patients |

## Running

Requires Docker and the .NET SDK. Run from the root of the repository:

```sh
Tests/Performance/run-patients.sh postgres
Tests/Performance/run-patients.sh mongo

# Fewer patients for a quick check
COUNT=1000 Tests/Performance/run-patients.sh postgres

# Patients only, without the clinical resources
CLINICAL=false Tests/Performance/run-patients.sh postgres
```

Results are written to `Tests/Performance/results/<store>/`, with a `summary.txt` and the full k6 output of each step.

## Notes on a fair comparison

- A fresh `mongo:8` database has none of the indexes Spark needs, which makes MongoDB look far slower than it is. The
  script creates the indexes Spark ships with, plus `(@REFERENCE, @state)` and `(@typename, id, @state)`, without which
  every update scans the resources collection.
- Spark ships no indexes on the search fields of the MongoDB search index either, so every search scans the whole
  collection. The script adds one per search this test runs, which is what a tuned deployment would do. The PostgreSQL
  store indexes its search tables in its schema, so it needs nothing extra.
- Spark runs on the host, so the numbers depend on the machine. Compare the stores on the same machine rather than
  comparing absolute numbers between machines.
