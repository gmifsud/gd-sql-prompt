INSERT INTO dbo.t (a, b)
VALUES (1, 2), (3, 4);
INSERT INTO dbo.t (a)
SELECT x
FROM dbo.s
WHERE y IS NULL;
UPDATE t
SET a = 1, b = b+1
FROM dbo.t t
    JOIN dbo.u u ON u.id = t.id
WHERE t.c = 3;
DELETE FROM dbo.t
WHERE a IN (
    SELECT a
    FROM dbo.gone
);
