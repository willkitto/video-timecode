@echo off
rem Runs Video Timecode straight away with the settings saved from its window
start "" "%~dp0VideoTimecode.exe" --use-saved %*
