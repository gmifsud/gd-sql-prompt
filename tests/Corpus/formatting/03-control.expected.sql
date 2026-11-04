DECLARE @i int = 0, @name nvarchar(50);
WHILE @i < 3
BEGIN
    SET @i += 1
END
IF EXISTS (
    SELECT 1
    FROM dbo.t
)
BEGIN
    SELECT 1;
END
ELSE IF @i = 2
    SELECT 2;
ELSE
    SELECT 3;
BEGIN try
    SELECT 1/0;
END try
BEGIN catch
    throw;
END catch
