| shape | edge load from DB (ms, cold cache miss) | BFS all users total (ms) | closure rows | closure build (ms) | move-subtree: adjacency+cycle check (ms) | move-subtree: closure rewrite (ms) | move-subtree: path rewrite (ms) | moved subtree size |
|---|---|---|---|---|---|---|---|---|
| 250 users, depth 8 | 5,26 | 0,0 (sum=1438) | 1438 | 62 | 16,52 | 9,99 | 10,66 | 11 |
| 10k users, depth 10 | 19,18 | 19,1 (sum=84548) | 84548 | 2101 | 13,83 | 23,64 | 45,10 | 448 |
| 10k users, flat (9999 direct reports) | 8,85 | 0,4 (sum=19999) | 19999 | 206 | 3,93 | 2,30 | 5,21 | 1 |
