create procedure dbo.p
    @a int,
    @b int = null
as
begin
set nocount on;
select @a, @b
end
