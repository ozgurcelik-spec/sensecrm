| shape | who (subtree size) | CTE wall p50 ms | closure wall p50 ms | path LIKE wall p50 ms | in-memory BFS p50 µs (graph cached) |
|---|---|---|---|---|---|
| 250 users, depth 8 | root (250) | 26,47 | 35,70 | 18,83 | 8,1 |
| 250 users, depth 8 | mid (11) | 25,37 | 14,09 | 16,49 | 0,3 |
| 250 users, depth 8 | leaf (1) | 10,24 | 7,66 | 5,52 | 0,1 |
| 10k users, depth 10 | root (10000) | 50,51 | 19,90 | 13,59 | 330,6 |
| 10k users, depth 10 | mid (448) | 3,35 | 1,70 | 6,16 | 7,9 |
| 10k users, depth 10 | leaf (1) | 1,72 | 1,35 | 5,17 | 0,0 |
| 10k users, flat (9999 direct reports) | root (10000) | 22,83 | 7,36 | 11,00 | 74,9 |
| 10k users, flat (9999 direct reports) | mid (1) | 1,60 | 1,32 | 4,62 | 0,0 |
| 10k users, flat (9999 direct reports) | leaf (1) | 2,01 | 1,48 | 4,54 | 0,0 |
