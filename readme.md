# PoC of Open Agent Management Protocol (OPAMP) server dotnet

How to run OPAMP server:

- `dotnet build`
- `dotnet run`

Run OTEL collector with OPAMP Extension (client):

- `cd collector`
- `./run.sh`

Known issues:

- OPAMP OTEL collector extension does not support remote configuration. To actually use it you need to configure OPAMP in supervisor mode.
- For some reason AgentResponseMessage have zero byte at beginning, had to remove it manually.

Note:  All the code generated by LLM, use with care.
