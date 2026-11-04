WITH c AS (
    SELECT id, name
    FROM dbo.customer
    WHERE active = 1
)
SELECT DISTINCT TOP (10) c.id, c.name, count(*) AS orders
FROM c
    INNER JOIN dbo.orders o ON o.customer_id = c.id AND o.status <> 'X'
    LEFT JOIN (SELECT 1 AS z) d ON 1 = 1
WHERE c.name LIKE 'A%'
GROUP BY c.id, c.name
HAVING count(*) > 1
ORDER BY orders DESC;
