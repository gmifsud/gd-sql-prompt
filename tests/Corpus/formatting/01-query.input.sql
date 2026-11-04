with c as (select id, name from dbo.customer where active=1)
select distinct top (10) c.id, c.name, count(*) as orders from c inner join dbo.orders o on o.customer_id=c.id and o.status<>'X' left join (select 1 as z) d on 1=1 where c.name like 'A%' group by c.id, c.name having count(*)>1 order by orders desc;
