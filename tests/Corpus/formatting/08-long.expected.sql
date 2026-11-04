SELECT
    customer_identifier,
    customer_display_name,
    customer_primary_email_address,
    customer_created_at_utc,
    customer_status
FROM dbo.customer
WHERE customer_status = 'active'
    AND customer_created_at_utc >= '2026-01-01'
    AND customer_primary_email_address IS NOT NULL
