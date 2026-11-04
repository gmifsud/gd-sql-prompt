CREATE PROCEDURE dbo.p
    @a int,
    @b int = NULL
AS
BEGIN
    SET nocount ON;
    SELECT @a, @b
END
