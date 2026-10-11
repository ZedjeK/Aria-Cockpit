@echo off
rem Aria - double-clic : installe .NET si besoin, compile Aria et ouvre l'interface.
rem Rien n'est modifie tant que vous ne cliquez pas sur "Appliquer" dans l'application.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0outils\Lancer-Aria.ps1"
