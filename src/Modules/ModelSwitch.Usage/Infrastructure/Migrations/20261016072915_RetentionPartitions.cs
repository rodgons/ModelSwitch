using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetentionPartitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The retention job's partition management (data-model.md, "Partitioning" and "Retention"). The worker's roles cannot run DDL,
            // so this function runs as its owner, the migrator role (SECURITY DEFINER, with a fixed search_path). It creates the monthly
            // partitions from the current month to months_ahead after it, then drops every partition of inference_request and
            // provider_attempt whose whole month ended before now - retention_days. A partition whose lock cannot be had within 5 s is
            // left for the next run. It returns the names it dropped. EXECUTE is revoked from PUBLIC; the Migrator grants it to the
            // app and retention roles.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION usage.maintain_request_partitions(retention_days integer, months_ahead integer) RETURNS SETOF text
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                DECLARE
                    cutoff timestamptz;
                    part record;
                    ym text[];
                    month_end timestamptz;
                BEGIN
                    IF retention_days IS NULL OR retention_days < 1 OR months_ahead IS NULL OR months_ahead < 0 OR months_ahead > 24 THEN
                        RAISE EXCEPTION 'retention_days must be >= 1 and months_ahead between 0 and 24';
                    END IF;

                    PERFORM usage.ensure_request_partitions(0, months_ahead);
                    cutoff := now() - make_interval(days => retention_days);

                    FOR part IN
                        SELECT child.relname AS name
                        FROM pg_inherits i
                        JOIN pg_class child ON child.oid = i.inhrelid
                        JOIN pg_class parent ON parent.oid = i.inhparent
                        JOIN pg_namespace ns ON ns.oid = parent.relnamespace
                        WHERE ns.nspname = 'usage' AND parent.relname IN ('inference_request', 'provider_attempt')
                          AND child.relname ~ '_y[0-9]{4}m[0-9]{2}$'
                        ORDER BY child.relname
                    LOOP
                        -- The partition of month M of year Y holds [Y-M-01, the first of the next month) in UTC.
                        ym := regexp_match(part.name, '_y([0-9]{4})m([0-9]{2})$');
                        month_end := make_timestamptz(ym[1]::integer + ym[2]::integer / 12, ym[2]::integer % 12 + 1, 1, 0, 0, 0, 'UTC');
                        IF month_end <= cutoff THEN
                            BEGIN
                                SET LOCAL lock_timeout = '5s';
                                EXECUTE format('DROP TABLE usage.%I', part.name);
                                RETURN NEXT part.name;
                            EXCEPTION WHEN lock_not_available THEN
                                RAISE NOTICE 'partition % is busy; it is left for the next run', part.name;
                            END;
                        END IF;
                    END LOOP;
                END
                $fn$;

                REVOKE ALL ON FUNCTION usage.maintain_request_partitions(integer, integer) FROM PUBLIC;
                REVOKE ALL ON FUNCTION usage.ensure_request_partitions(integer, integer) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS usage.maintain_request_partitions(integer, integer);");
        }
    }
}
