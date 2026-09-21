| owners | variant | list25 wall p50 ms | list25 wall p95 ms | list25 plan exec ms | count wall p50 ms | count plan exec ms | rows(count) |
|---|---|---|---|---|---|---|---|
| 6 | A = ANY(@o)  [recommended] | 1,70 | 1,98 | 0,27 | 9,01 | 6,65 | 4698 |
| 6 | B JOIN unnest(@o) | 2,04 | 2,57 | 2,00 | 7,86 | 6,11 | 4698 |
| 6 | C IN (SELECT unnest(@o)) | 2,49 | 3,17 | 0,77 | 13,57 | 14,67 | 4698 |
| 6 | D persisted set table | 5,61 | 16,42 | 0,82 | 14,62 | 7,45 | 4698 |
| 6 | E literal IN (EF Constant mode) | 2,01 | 3,13 | 0,30 | 9,09 | 8,88 | 4698 |
| 6 | F per-request TEMP table | 14,71 | 19,87 | 0,93 | 26,15 | 9,85 | 4698 |
| 201 | A = ANY(@o)  [recommended] | 9,44 | 19,37 | 0,09 | 41,60 | 37,18 | 157379 |
| 201 | B JOIN unnest(@o) | 2,03 | 3,94 | 0,62 | 44,38 | 61,72 | 157379 |
| 201 | C IN (SELECT unnest(@o)) | 2,45 | 3,76 | 0,41 | 36,93 | 77,55 | 157379 |
| 201 | D persisted set table | 5,91 | 21,44 | 0,17 | 38,99 | 38,23 | 157379 |
| 201 | E literal IN (EF Constant mode) | 2,76 | 4,55 | 0,09 | 29,83 | 37,83 | 157379 |
| 201 | F per-request TEMP table | 11,88 | 17,15 | 0,14 | 50,14 | 55,11 | 157379 |
| 2001 | A = ANY(@o)  [recommended] | 5,17 | 19,68 | 0,25 | 42,78 | 46,21 | 195745 |
| 2001 | B JOIN unnest(@o) | 6,50 | 10,63 | 4,03 | 163,08 | 177,84 | 195745 |
| 2001 | C IN (SELECT unnest(@o)) | 8,44 | 17,26 | 0,43 | 80,91 | 90,06 | 195745 |
| 2001 | D persisted set table | 15,03 | 28,34 | 0,30 | 69,38 | 75,21 | 195745 |
| 2001 | E literal IN (EF Constant mode) | 12,43 | 28,42 | 0,23 | 51,74 | 49,22 | 195745 |
| 2001 | F per-request TEMP table | 20,73 | 29,79 | 0,25 | 54,46 | 72,80 | 195745 |
| 20001 | A = ANY(@o)  [recommended] | 21,29 | 45,08 | 1,06 | 56,50 | 50,26 | 195745 |
| 20001 | B JOIN unnest(@o) | 19,66 | 37,97 | 41,43 | 166,58 | 162,18 | 195745 |
| 20001 | C IN (SELECT unnest(@o)) | 244,19 | 371,91 | 313,27 | 76,26 | 77,81 | 195745 |
| 20001 | D persisted set table | 2,00 | 2,59 | 0,30 | 41,06 | 72,01 | 195745 |
| 20001 | F per-request TEMP table | 65,49 | 99,91 | 0,21 | 251,35 | 162,00 | 195745 |
