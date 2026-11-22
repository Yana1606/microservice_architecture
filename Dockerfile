FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Host/Host.csproj -c Release -o /app/host && dotnet publish src/Simulator/Simulator.csproj -c Release -o /app/simulator
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/host ./host
COPY --from=build /app/simulator ./simulator
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet"]
CMD ["host/Host.dll"]
