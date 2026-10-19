@echo off
rem One-click setup for the OpportunityPilot desktop agent on Windows.
rem Installs what it needs, asks for your agent key (Desktop agent card -> Connect the agent) and saves it in config.json.
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js is needed first: install the LTS version from https://nodejs.org and run this again. & pause & exit /b 1)
echo Installing the agent (one minute)...
call npm install --no-fund --no-audit || (echo npm install failed. & pause & exit /b 1)
set /p OPKEY=Paste your agent key (starts with opk_) and press Enter: 
call npm run agent -- init --key %OPKEY% || (pause & exit /b 1)
call npm run agent -- campaigns || (echo Could not reach the web app with that key. Make a new key on the Desktop agent card and run this again. & pause & exit /b 1)
echo.
echo Connected. Next: sign in once to each site in the window that opens.
call npm run agent -- login linkedin
echo.
echo Done. Edit config.json "answers" (notice period, salary, phone, years per skill), then:
echo   npm run agent -- apply linkedin            (dry run, sends nothing)
echo   npm run agent -- apply linkedin --submit --limit 2
pause
