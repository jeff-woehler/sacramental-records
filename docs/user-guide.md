# User guide

## Before you begin

Sacramental Records is a Windows desktop application for parish staff. It stores parish data in a portable `MemberRecords.srdb` package. Microsoft Word is required only for baptism certificate generation.

Install the release ZIP on the office PC and start the application executable. The program does not include parish data. On first use, an administrator should configure the package path and confirm that the account has read/write access to the shared folder.

## Open the parish registry

1. Start the application.
2. Select **Open** and choose the parish `.srdb` package.
3. Wait for the status indicator to show that the data source is open.
4. Search by given name or family name, then select a person and choose **Edit** or double-click the result.

Only one application session should edit a package at a time. Keep the application open while making a series of changes; records are saved to the local session as you work.

## Maintain a person

Choose **Add** to create a person, or **Change** from the person record to edit one. Given name and family name are required. Add the birth date, birth city, status, and parish identifiers when known. A person-level baptism date and church are for baptism details received from another parish; when the baptism took place in a register managed here, create a baptism entry instead.

Use **Register Entries** to add or edit baptism, confirmation, first communion, marriage, religious profession, diaconate, or another supported entry type. Enter the date, place, minister, family, sponsors/witnesses, register location, and notes as applicable. Review the entry before saving. Deleting a person also deletes that person’s entries.

## Generate a baptism certificate

Open a person with a baptism record and choose **Certificate**. Word evaluates the included template and exports the certificate as a PDF. If the parish uses a customized template, an administrator can select it through **Admin > Settings**. Certificate generation does not change the parish database.

## Publish and close

Choose **Save** on the main window to publish the local session to the selected package. Publishing creates a verified package and retains a backup of the previous package. Continue working after a successful publish if needed.

When closing, publish any remaining work, discard the local session, or continue working. Do not manually copy, rename, edit, or open the package in another application while a session is active. If a publish fails, leave the application open and retry after resolving the file or permissions problem.

## Keyboard shortcuts

- `Ctrl+O` — open a package
- `Ctrl+F` — focus search
- `Enter` — search or open the selected record
- `Ctrl+N` — add a person or entry in the relevant view
- `Ctrl+E` — edit the current person
- `Ctrl+S` — save the current editor
- `Esc` — cancel the current editor
- `Delete` — delete the selected entry after confirmation

For operational safeguards and recovery procedures, see [Administration and data management](administration.md).
