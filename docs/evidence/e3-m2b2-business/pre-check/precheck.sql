\pset footer off
SELECT 'fev_total' k, count(*) v FROM field_encrypted_values
UNION ALL SELECT 'fev_tie_mismatch', count(*) FROM field_encrypted_values WHERE row_signature_schema_version <> encryption_envelope_version
UNION ALL SELECT 'fev_envelope_not_in_1_2', count(*) FROM field_encrypted_values WHERE encryption_envelope_version NOT IN (1,2)
UNION ALL SELECT 'fev_envelope_not_1', count(*) FROM field_encrypted_values WHERE encryption_envelope_version <> 1
UNION ALL SELECT 'fev_schema_not_1', count(*) FROM field_encrypted_values WHERE row_signature_schema_version <> 1
UNION ALL SELECT 'fav_total', count(*) FROM file_attachment_versions
UNION ALL SELECT 'fav_processed', count(*) FROM file_attachment_versions WHERE version_state = 'Processed'
UNION ALL SELECT 'fav_tie_mismatch', count(*) FROM file_attachment_versions WHERE row_signature_schema_version <> encryption_envelope_version
UNION ALL SELECT 'fav_envelope_not_in_1_2', count(*) FROM file_attachment_versions WHERE encryption_envelope_version NOT IN (1,2)
UNION ALL SELECT 'fav_envelope_not_1', count(*) FROM file_attachment_versions WHERE encryption_envelope_version <> 1
UNION ALL SELECT 'fav_schema_not_1', count(*) FROM file_attachment_versions WHERE row_signature_schema_version <> 1;
SELECT 'columns_already_present' k, count(*) v FROM information_schema.columns WHERE table_name IN ('field_encrypted_values','file_attachment_versions') AND column_name IN ('data_key_identifier','data_key_commitment_bytes','key_wrap_algorithm_code');
SELECT 'latest_migration' k, max("MigrationId") v FROM "__EFMigrationsHistory";
