declare @i int=0, @name nvarchar(50);
while @i<3 begin set @i+=1 end
if exists (select 1 from dbo.t) begin select 1; end else if @i=2 select 2; else select 3;
begin try select 1/0; end try begin catch throw; end catch
