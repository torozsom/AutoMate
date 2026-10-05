namespace Infrastructure.Data;

/// <summary>Database boundary capture covers tracked saves, direct SQL, bulk cleanup and multiple deployment workers.</summary>
internal static class FailedDeploymentAnalysisTrigger
{
    /// <summary>Captures only actual failures, atomically with the status write; one marker survives result/receipt cleanup.</summary>
    internal const string CreateSql = """
                                      CREATE FUNCTION capture_failed_deployment_analysis() RETURNS trigger LANGUAGE plpgsql AS $body$
                                      BEGIN
                                          IF TG_OP = 'UPDATE' THEN
                                              IF OLD.status IS NOT DISTINCT FROM NEW.status THEN
                                                  RETURN NEW;
                                              END IF;
                                          END IF;
                                          IF NEW.status = 3 THEN
                                              INSERT INTO failed_deployment_analysis_events (deployment_id, created_at)
                                              VALUES (NEW.id, CURRENT_TIMESTAMP)
                                              ON CONFLICT (deployment_id) DO NOTHING;
                                          END IF;
                                          RETURN NEW;
                                      END;
                                      $body$;
                                      CREATE TRIGGER capture_failed_deployment_analysis
                                      AFTER INSERT OR UPDATE OF status ON deployments
                                      FOR EACH ROW EXECUTE FUNCTION capture_failed_deployment_analysis();
                                      """;

    /// <summary>Removes capture before dropping its metadata table.</summary>
    internal const string DropSql = """
                                    DROP TRIGGER capture_failed_deployment_analysis ON deployments;
                                    DROP FUNCTION capture_failed_deployment_analysis();
                                    """;
}