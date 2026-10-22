CREATE OR ALTER PROCEDURE dbo.usp_GetOrders
    @CustomerId INT,
    @Since DATETIME2 = NULL
AS
BEGIN
    SET NOCOUNT ON;

    WITH Recent AS (
        SELECT o.OrderId, o.CustomerId, o.OrderDate
        FROM dbo.Orders AS o
        WHERE o.CustomerId = @CustomerId
          AND (@Since IS NULL OR o.OrderDate >= @Since)
    )
    SELECT r.*, c.Name
    FROM Recent AS r
    INNER JOIN dbo.Customers AS c ON c.CustomerId = r.CustomerId
    ORDER BY r.OrderDate DESC;
END
GO
