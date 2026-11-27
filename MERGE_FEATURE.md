# Record Merge Feature Implementation

## Overview
Added comprehensive record merge capability to the Sacramental Records application. This feature allows users to merge duplicate person records with conflict resolution and automatic sacrament record reassignment.

## Features Implemented

### 1. UI Changes

#### MainWindow.xaml
- Added [Merge] button next to [Edit] button in the search panel
- Merge button is initially disabled
- Merge button becomes active only when exactly 2 records are selected
- Updated ListBox selection mode to "Extended" to support multi-select

#### Ctrl+Click Multi-Selection
- Users can hold Ctrl and click to select/deselect multiple records
- Each Ctrl+click toggles the selected state of that record
- Selection highlighting shows which records are selected
- Merge button automatically enables/disables based on selection count

### 2. Core Merge Logic (MergeService.cs)

#### MergeResult Class
- Encapsulates merge operation results
- Tracks conflicting fields
- Maintains reference to both records being merged
- Stores list of sacrament records to be reassigned

#### MergeService Class
- **DetectConflicts()**: Compares two Person records and identifies conflicting fields
  - Checks all person attributes for differences
  - Returns a MergeResult with conflict details
  - Reports success if all fields match exactly
  
- **ResolveMerge()**: Creates merged person from conflict resolutions
  - Takes user choices for each conflicting field
  - Applies left/right selections
  - Returns finalized merged Person record

### 3. Sacrament Record Handling

**Critical Feature**: All sacrament records are automatically reassigned to the merged person:

1. **Fetch Phase**: Before merge, retrieves all sacrament records for both people
2. **Merge Phase**: Saves merged person record with selected attribute values
3. **Reassignment Phase**: Updates all sacrament records from duplicate person:
   - Changes `PersonRecordId` to point to merged person
   - Updates timestamp to current UTC time
   - Preserves all sacrament data (type, date, minister, etc.)
4. **Cleanup Phase**: Deletes duplicate person record
5. **Verification**: Status message shows count of sacraments reassigned

**Examples**:
- If Person A has 3 baptism records and Person B has 2 confirmation records:
  - After merge, the final record will have 5 sacrament records total
- If both have records of the same sacrament type with same date, both are preserved
  - User can manually manage duplicates through the person detail view if needed

### 4. Conflict Resolution Window (MergeConflictWindow)

#### Visual Design
- Uses existing application theming (consistent colors and styling)
- Displays conflicting fields in an organized layout
- Shows left value vs. right value side-by-side
- Professional presentation with clear visual hierarchy

#### Functionality
- Automatically builds UI based on detected conflicts
- Each conflicting field shows:
  - Field name
  - Left person's value (initially selected by default)
  - Right person's value
  - Radio buttons to choose left or right
- Two buttons:
  - [Cancel]: Abandons merge operation
  - [Merge]: Confirms selections and proceeds

### 5. Merge Workflow

1. User selects exactly 2 records using Ctrl+click
2. [Merge] button becomes enabled
3. User clicks [Merge]
4. System fetches sacrament records for both people
5. System detects conflicts automatically
6. If no conflicts: 
   - Records are merged immediately
   - Sacrament records are reassigned
   - Duplicate record is deleted
   - Success message displays with sacrament count
7. If conflicts exist: 
   - Conflict resolution window opens
   - User reviews each conflict
   - Chooses preferred value for each field (default is left)
   - Clicks [Merge] or [Cancel]
   - If [Merge] confirmed: 
     - Records are merged with selected values
     - Sacrament records are reassigned
     - Duplicate record is deleted
   - If [Cancel]: Operation abandoned

### 6. Data Persistence

- Merged record is saved with current timestamp
- All sacrament records are updated with new PersonRecordId
- Sacrament records retain all their original data
- Duplicate person record is deleted from database
- Sacrament records are never deleted (preserved for historical accuracy)
- Changes follow standard publish workflow
- Maintains referential integrity

## Files Created/Modified

### New Files:
1. **MergeService.cs** - Core merge logic and conflict detection
2. **MergeConflictWindow.xaml** - Conflict resolution UI definition
3. **MergeConflictWindow.xaml.cs** - Conflict resolution logic

### Modified Files:
1. **MainWindow.xaml** - Added Merge button, updated ListBox settings
2. **MainWindow.xaml.cs** - Added merge handling, Ctrl+click support, and sacrament reassignment

## Technical Details

### Supported Merge Fields:
- Given Name
- Middle Name
- Family Name
- Birth Date
- Birth City
- Baptism Date
- Baptism Church
- Status
- Maiden Name
- Deceased Date
- Church DB ID
- PDS Member ID
- Flocknote ID

### Sacrament Handling:
- Automatically fetches sacrament records for both people before merge
- Uses record `with` expression to create updated sacrament records
- Changes PersonRecordId to point to merged record
- Updates sacrament timestamp to current UTC
- Preserves all sacrament fields (type, date, minister, sponsors, etc.)
- Never deletes sacrament records (data preservation)
- Handles any number of sacraments (0 to many)

### Selection Mechanism:
- PreviewMouseDown event handler detects Ctrl+click
- Uses VisualTreeHelper to find clicked item
- Maintains manual SelectedItems collection management
- SelectionChanged event updates Merge button state

### Conflict Detection:
- Null values are treated as equivalent to default
- Exact string/date comparison
- Any difference triggers conflict flag
- All conflicts presented to user

## User Experience

### Keyboard Shortcuts:
- Ctrl+Click: Toggle selection of individual records
- Enter: Open selected record (unless Ctrl is held)
- Double-click: Open selected record

### Status Messages:
- "Records merged successfully (X sacrament records reassigned)." - On successful merge
- "Please select exactly two people to merge." - When selection count != 2
- "Merge failed: [error]" - When operation encounters error
- "Fetching sacrament records..." - During initial fetch
- "Saving merged record..." - During person save
- "Reassigning sacrament records..." - During sacrament updates

## Important Considerations

### Data Preservation:
- Sacrament records are NEVER deleted during merge
- All historical sacrament data is preserved
- Reassignment is reversible through direct database edit if needed
- Both person records must have matching sacrament for data loss prevention

### Merge Strategy:
- Keep person with more sacraments as the primary record?
  - No automatic preference - user chooses via conflict resolution
  - If no conflicts in person data, merge proceeds with left person retained
- Duplicate person record is deleted after sacraments reassigned
- Orphaned sacraments (if any) would be automatically adopted

### Conflict Scenarios:
- If both people have exact same sacrament data, one copy is preserved
- If different details for same sacrament type, both records kept (user must manually consolidate)
- User can review/edit sacraments after merge in person detail view

## Testing Recommendations

1. Test merging records with no conflicts and no sacraments
2. Test merging records where only one has sacraments
3. Test merging records where both have different sacraments
4. Test merging records with overlapping sacraments (same type/date)
5. Verify sacrament count in status message is accurate
6. Verify merged person shows all sacraments in detail view
7. Test canceling merge operation (sacraments unchanged)
8. Verify sacrament timestamps are updated to merge time
9. Test with read-only database (should fail gracefully)
10. Verify merged person appears in search results

## Compatibility

- .NET 8.0 Windows (net8.0-windows)
- WPF Framework
- Uses existing RegistryService for data operations
- Compatible with existing theming system
- No breaking changes to existing functionality
- Fully integrated with sacrament persistence layer

## Future Enhancements

Potential improvements for future versions:
1. Show preview of sacrament records before merge confirmation
2. Allow selective sacrament reassignment (pick which to merge)
3. Automatic duplicate sacrament detection and merging
4. Audit log for merge operations (who merged, when, which records)
5. Undo capability for recent merges
6. Bulk merge for multiple record pairs

