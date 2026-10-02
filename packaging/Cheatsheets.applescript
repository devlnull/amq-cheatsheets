-- Cheatsheets launcher: Spotlight (⌘Space) -> "Cheatsheets" -> type a query -> pick -> opens the sheet.
-- __CS__ is replaced with the absolute path of the installed `cs` by `cs app`.
on run
	set csPath to "__CS__"
	set pathPrefix to "export PATH=/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:$PATH; "
	tell me to activate
	set q to text returned of (display dialog "Search cheatsheets (title, tag or shortcut):" default answer "" with title "Cheatsheets" buttons {"Cancel", "Search"} default button "Search")
	try
		set out to do shell script pathPrefix & quoted form of csPath & " search --lines " & quoted form of q
	on error
		set out to ""
	end try
	if out is "" then
		display dialog "No cheatsheet matches “" & q & "”." with title "Cheatsheets" buttons {"OK"} default button "OK"
		return
	end if
	set ids to {}
	set labels to {}
	set AppleScript's text item delimiters to tab
	repeat with l in paragraphs of out
		set parts to text items of (l as text)
		if (count of parts) ≥ 2 then
			set end of ids to item 1 of parts
			set end of labels to item 2 of parts
		end if
	end repeat
	set AppleScript's text item delimiters to ""
	set picked to choose from list labels with title "Cheatsheets" with prompt "Open which cheatsheet?" default items {item 1 of labels}
	if picked is false then return
	repeat with i from 1 to count of labels
		if item i of labels is (item 1 of picked) then
			do shell script pathPrefix & quoted form of csPath & " open --id " & quoted form of (item i of ids)
			exit repeat
		end if
	end repeat
end run
