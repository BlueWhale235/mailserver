FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props MailServer.slnx ./
COPY src ./src
COPY plugins ./plugins
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release --no-restore -o /app
RUN dotnet restore "plugins/MailServer.MimePlugin/MailServer.MimePlugin.csproj"
RUN dotnet publish "plugins/MailServer.MimePlugin/MailServer.MimePlugin.csproj" -c Release --no-restore -o /mime-plugin
RUN dotnet restore "plugins/MailServer.GreylistingPlugin/MailServer.GreylistingPlugin.csproj"
RUN dotnet publish "plugins/MailServer.GreylistingPlugin/MailServer.GreylistingPlugin.csproj" -c Release --no-restore -o /greylisting-plugin

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
ENV APP_DLL="${PROJECT}.dll"
WORKDIR /app
COPY --from=build /app .
COPY --from=build /mime-plugin ./plugins/MailServer.MimePlugin
COPY --from=build /greylisting-plugin ./plugins/MailServer.GreylistingPlugin
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
