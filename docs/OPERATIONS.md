# Operations checklist

## Before pilot

- Obtain diocesan approval for an Entra public-client registration.
- Restrict the OneDrive/SharePoint folder to the registry team.
- Create the workbook and exact table headers in `README.md`.
- Decide who may import, edit, issue certificates, and administer access.
- Test simultaneous edits from two PCs and verify conflict refusal.
- Establish a weekly versioned copy outside the live workbook.

## Daily use

- Open the desktop app; do not open the live workbook in Excel.
- Sign in with the assigned diocesan account.
- Search before creating a new person.d
- Review the record and use the application certificate action.
- If a conflict or network error appears, refresh; do not retry blindly.

## Recovery

The workbook's version history is the first recovery mechanism. An administrator should also make a dated copy before bulk imports and retain import source files. Restore into a new workbook, validate counts, then change the configured item ID rather than replacing a file while clients are running.
