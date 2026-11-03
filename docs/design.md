# Design decisions and operations

## Scoring

Each category matches at most once: .NET/C# 25; Azure 15; SQL 15; Angular/React/TypeScript 10; API/integration/webhook 15; automation/AI/n8n 10; Docker/CI/CD/DevOps 10. The maximum is 100. The labels describe interest and keyword alignment, not verified experience or probability of getting hired.

`CAREER_MIN_HOURLY_USD` defaults to 10. `CAREER_INBOX_DATA` controls the private data directory. `CAREER_PORT` defaults to 5680 (change the n8n HTTP URLs too if changing the port). Hourly rate is nullable; a blank rate is never interpreted as zero. Confirm current preferences before using the tracker to exclude opportunities.

Eligibility is yes/no/unknown, explicitly entered by the owner. Vacancy status is open/closed/unverified. Recognized closed notices override an incorrectly entered open value. A known closed vacancy, ineligible location, or rate below the minimum is excluded. Unknown values remain visible with warnings.

## Data model

`jobs`: integer ID, unique normalized URL, title, company, description, nullable hourly USD, eligibility, vacancy status, application status, score, reasons, UTC timestamps.

`events`: append-only capture/status history with job ID, event name, note and UTC timestamp. Application statuses: new, shortlisted, applied, interview, rejected, withdrawn, closed, excluded.

One transaction inserts the job and capture event. SQLite uniqueness is the final duplicate safeguard. Replaying a capture returns the existing ID and preserves original details. Status updates and audit events share a transaction. Status updates are not retried automatically by n8n because duplicate audit events would be misleading.

## Failure handling

Capture retries up to three times with a one-second wait for transient errors. Duplicate protection makes capture retries safe. Form HTTP nodes route errors to a completion message that explicitly avoids claiming success. Inspect n8n executions for details. Scheduled digest errors fail the execution visibly. There is no external alert channel configured.

Digest writes use a temporary file and atomic replace. Re-running creates no job rows. API validation returns HTTP 400. Unexpected server exceptions remain HTTP 500 and are logged locally. Requests larger than 256 KiB are rejected by the server.

## Deployment and recovery

Start the API and n8n, import/publish the three workflows, then submit a synthetic listing. Capture the same URL twice and confirm the same ID. Mark it applied and inspect the audit trail. Run the digest manually. Restart the API and confirm the saved row persists.

The API is intentionally local only. Publishing a workflow in n8n enables the local production form route; it does not make localhost reachable from the internet. Schedules execute only while n8n and the computer are awake.

For recovery, stop the API and copy its entire private data directory. Keep a separate stopped backup of n8n data. Source-controlled workflow JSONs contain no credentials or execution data. Reimporting workflows with fixed IDs can overwrite their corresponding imported definitions; export local edits privately first.
