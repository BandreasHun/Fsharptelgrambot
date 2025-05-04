# WepeBot

WepeBot is a Telegram bot written in F# for tracking and managing "alpha calls" (cryptocurrency contract announcements) with live price updates from Dexscreener. It supports adding, listing, removing, and updating tracked tokens, and can periodically post updates to a Telegram chat.

## Features

- Add new alpha calls by extracting contract address and chain from a description (uses OpenAI API).
- Fetches live price and token info from Dexscreener.
- List all tracked alpha calls with current price, start price, and percentage change.
- Remove or update alpha calls by index.
- Periodic update loop to post alpha call status at a set interval.
- Admin-only command access.

## Commands

All commands must be issued by a Telegram group admin.

- `/addalphacall <description>`  
  Extracts contract address and chain from the description, fetches token info, and adds it to the tracked list.

- `/getalpha`  
  Lists all tracked alpha calls with live price and stats.

- `/removecall <index>`  
  Removes the alpha call at the given index (see `/getalpha` for indices).

- `/changestart <index> <newprice>`  
  Changes the start price for the alpha call at the given index.

- `/start`  
  Starts the periodic update loop (posts alpha call status every interval).

- `/stop`  
  Stops the periodic update loop.

- `/interval <minutes>`  
  Sets the interval (in minutes) for the periodic update loop.

## How It Works

1. **Adding an Alpha Call:**  
   When you use `/addalphacall`, the bot uses OpenAI to extract the contract address and chain from your description. It then queries Dexscreener for token info and adds it to the tracked list.

2. **Listing Alpha Calls:**  
   `/getalpha` fetches the latest price for each tracked token from Dexscreener and displays the current price, start price, and percentage change.

3. **Removing/Updating Calls:**  
   Use `/removecall` or `/changestart` with the index shown in `/getalpha` to manage tracked calls.

4. **Periodic Updates:**  
   Use `/start` to begin periodic posting of alpha call status. Use `/interval` to set how often updates are posted. Use `/stop` to end the loop.

5. **Persistence:**  
   The bot saves tracked alpha calls to state.json and reloads them on startup.

## Setup

1. Set the following environment variables:
   - `TELEGRAM_TOKEN` – your Telegram bot token.
   - `OPENAI_API_KEY` – your OpenAI API key.

2. Build and run the bot:
   ```
   dotnet build
   dotnet run
   ```

3. Add the bot to your Telegram group and promote it to admin.

## Notes

- Only group admins can use the commands.
- The bot requires internet access to query OpenAI and Dexscreener APIs.
- All data is stored locally in state.json
