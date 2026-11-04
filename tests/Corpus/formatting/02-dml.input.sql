insert into dbo.t (a,b) values (1,2),(3,4);
insert into dbo.t (a) select x from dbo.s where y is null;
update t set a=1, b=b+1 from dbo.t t join dbo.u u on u.id=t.id where t.c=3;
delete from dbo.t where a in (select a from dbo.gone);
