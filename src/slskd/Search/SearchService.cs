﻿// <copyright file="SearchService.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Search
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.EntityFrameworkCore;
    using Serilog;
    using slskd.Search.API;
    using Soulseek;
    using SearchOptions = Soulseek.SearchOptions;
    using SearchQuery = Soulseek.SearchQuery;
    using SearchScope = Soulseek.SearchScope;
    using SearchStates = Soulseek.SearchStates;

    /// <summary>
    ///     Handles the lifecycle and persistence of searches.
    /// </summary>
    public interface ISearchService
    {
        /// <summary>
        ///     Deletes the specified search.
        /// </summary>
        /// <param name="search">The search to delete.</param>
        /// <returns>The operation context.</returns>
        Task DeleteAsync(Search search);

        /// <summary>
        ///     Finds a single search matching the specified <paramref name="expression"/>.
        /// </summary>
        /// <param name="expression">The expression to use to match searches.</param>
        /// <param name="includeResponses">A value indicating whether to include search responses in the result.</param>
        /// <returns>The found search, or default if not found.</returns>
        /// <exception cref="ArgumentException">Thrown when an expression is not supplied.</exception>
        Task<Search> FindAsync(Expression<Func<Search, bool>> expression, bool includeResponses = false);

        /// <summary>
        ///     Returns a list of all completed and in-progress searches, with responses omitted, matching the optional <paramref name="expression"/>.
        /// </summary>
        /// <param name="expression">An optional expression used to match searches.</param>
        /// <returns>The list of searches matching the specified expression, or all searches if no expression is specified.</returns>
        Task<List<Search>> ListAsync(Expression<Func<Search, bool>> expression = null);

        /// <summary>
        ///     Performs a search for the specified <paramref name="query"/> and <paramref name="scope"/>.
        /// </summary>
        /// <param name="id">A unique identifier for the search.</param>
        /// <param name="query">The search query.</param>
        /// <param name="scope">The search scope.</param>
        /// <param name="options">Search options.</param>
        /// <returns>The completed search.</returns>
        Task<Search> StartAsync(Guid id, SearchQuery query, SearchScope scope, SearchOptions options = null);

        /// <summary>
        ///     Cancels the search matching the specified <paramref name="id"/>, if it is in progress.
        /// </summary>
        /// <param name="id">The unique identifier for the search.</param>
        /// <returns>A value indicating whether the search was successfully cancelled.</returns>
        bool TryCancel(Guid id);

        /// <summary>
        ///     Removes <see cref="SearchStates.Completed"/> searches older than the specified <paramref name="age"/>.
        /// </summary>
        /// <param name="age">The age after which records are eligible for pruning, in minutes.</param>
        /// <returns>The number of pruned records.</returns>
        Task<int> PruneAsync(int age);
    }

    /// <summary>
    ///     Handles the lifecycle and persistence of searches.
    /// </summary>
    public class SearchService : ISearchService
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="SearchService"/> class.
        /// </summary>
        /// <param name="searchHub"></param>
        /// <param name="optionsMonitor"></param>
        /// <param name="soulseekClient"></param>
        /// <param name="contextFactory">The database context to use.</param>
        public SearchService(
            IHubContext<SearchHub> searchHub,
            IOptionsMonitor<Options> optionsMonitor,
            ISoulseekClient soulseekClient,
            IDbContextFactory<SearchDbContext> contextFactory)
        {
            SearchHub = searchHub;
            OptionsMonitor = optionsMonitor;
            Client = soulseekClient;
            ContextFactory = contextFactory;
        }

        private ConcurrentDictionary<Guid, CancellationTokenSource> CancellationTokens { get; }
            = new ConcurrentDictionary<Guid, CancellationTokenSource>();

        private ISoulseekClient Client { get; }
        private IDbContextFactory<SearchDbContext> ContextFactory { get; }
        private ILogger Log { get; set; } = Serilog.Log.ForContext<Application>();
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private IHubContext<SearchHub> SearchHub { get; set; }

        /// <summary>
        ///     Deletes the specified search.
        /// </summary>
        /// <param name="search">The search to delete.</param>
        /// <returns>The operation context.</returns>
        public Task DeleteAsync(Search search)
        {
            if (search == default)
            {
                throw new ArgumentNullException(nameof(search));
            }

            return DoDeleteAsync(search);

            async Task DoDeleteAsync(Search search)
            {
                using var context = ContextFactory.CreateDbContext();
                context.Searches.Remove(search);
                context.SaveChanges();

                await SearchHub.BroadcastDeleteAsync(search);
            }
        }

        /// <summary>
        ///     Finds a single search matching the specified <paramref name="expression"/>.
        /// </summary>
        /// <param name="expression">The expression to use to match searches.</param>
        /// <param name="includeResponses">A value indicating whether to include search responses in the result.</param>
        /// <returns>The found search, or default if not found.</returns>
        /// <exception cref="ArgumentException">Thrown when an expression is not supplied.</exception>
        public Task<Search> FindAsync(Expression<Func<Search, bool>> expression, bool includeResponses = false)
        {
            if (expression == default)
            {
                throw new ArgumentException("An expression must be supplied.", nameof(expression));
            }

            using var context = ContextFactory.CreateDbContext();

            var selector = context.Searches
                .AsNoTracking()
                .Where(expression);

            if (!includeResponses)
            {
                selector = selector.WithoutResponses();
            }

            return selector.FirstOrDefaultAsync();
        }

        /// <summary>
        ///     Returns a list of all completed and in-progress searches, with responses omitted, matching the optional <paramref name="expression"/>.
        /// </summary>
        /// <param name="expression">An optional expression used to match searches.</param>
        /// <returns>The list of searches matching the specified expression, or all searches if no expression is specified.</returns>
        public Task<List<Search>> ListAsync(Expression<Func<Search, bool>> expression = null)
        {
            expression ??= s => true;
            using var context = ContextFactory.CreateDbContext();

            return context.Searches
                .AsNoTracking()
                .Where(expression)
                .WithoutResponses()
                .ToListAsync();
        }

        /// <summary>
        ///     Performs a search for the specified <paramref name="query"/> and <paramref name="scope"/>.
        /// </summary>
        /// <param name="id">A unique identifier for the search.</param>
        /// <param name="query">The search query.</param>
        /// <param name="scope">The search scope.</param>
        /// <param name="options">Search options.</param>
        /// <returns>The completed search.</returns>
        public async Task<Search> StartAsync(Guid id, SearchQuery query, SearchScope scope, SearchOptions options = null)
        {
            var token = Client.GetNextToken();

            var cancellationTokenSource = new CancellationTokenSource();
            CancellationTokens.TryAdd(id, cancellationTokenSource);

            var rateLimiter = new RateLimiter(250);

            var search = new Search()
            {
                SearchText = query.SearchText,
                Token = token,
                Id = id,
                State = SearchStates.Requested,
                StartedAt = DateTime.UtcNow,
            };

            using var context = ContextFactory.CreateDbContext();
            context.Add(search);
            context.SaveChanges();

            List<SearchResponse> responses = new();

            void UpdateAndSaveChanges(Search search)
            {
                using var context = ContextFactory.CreateDbContext();
                context.Update(search);
                context.SaveChanges();
            }

            options ??= new SearchOptions();
            options = options.WithActions(
                stateChanged: (args) =>
                {
                    search = search.WithSoulseekSearch(args.Search);
                    SearchHub.BroadcastUpdateAsync(search);
                    UpdateAndSaveChanges(search);
                },
                responseReceived: (args) => rateLimiter.Invoke(() =>
                {
                    // note: this is rate limited, but has the potential to update the database every 250ms (or whatever the
                    // interval is set to) for the duration of the search. any issues with disk i/o or performance while searches
                    // are running should investigate this as a cause
                    search.ResponseCount = args.Search.ResponseCount;
                    search.FileCount = args.Search.FileCount;
                    search.LockedFileCount = args.Search.LockedFileCount;

                    // note that we're not actually doing anything with the response here, that's happening in the
                    // response handler. we're only updating counts here.
                    SearchHub.BroadcastUpdateAsync(search);
                    UpdateAndSaveChanges(search);
                }));

            var soulseekSearchTask = Client.SearchAsync(
                query,
                responseHandler: (response) => responses.Add(response),
                scope,
                token,
                options,
                cancellationToken: cancellationTokenSource.Token);

            _ = Task.Run(async () =>
            {
                try
                {
                    var soulseekSearch = await soulseekSearchTask;
                    search = search.WithSoulseekSearch(soulseekSearch);
                }
                finally
                {
                    rateLimiter.Dispose();
                    CancellationTokens.TryRemove(id, out _);

                    try
                    {
                        search.EndedAt = DateTime.UtcNow;
                        search.Responses = responses.Select(r => Response.FromSoulseekSearchResponse(r));

                        UpdateAndSaveChanges(search);

                        // zero responses before broadcasting, as we don't want to blast this
                        // data out over the SignalR socket
                        await SearchHub.BroadcastUpdateAsync(search with { Responses = [] });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to persist search for {SearchQuery} ({Id})", query, id);
                    }
                }
            });

            await SearchHub.BroadcastCreateAsync(search);

            return search;
        }

        /// <summary>
        ///     Cancels the search matching the specified <paramref name="id"/>, if it is in progress.
        /// </summary>
        /// <param name="id">The unique identifier for the search.</param>
        /// <returns>A value indicating whether the search was successfully cancelled.</returns>
        public bool TryCancel(Guid id)
        {
            if (CancellationTokens.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Removes <see cref="SearchStates.Completed"/> searches older than the specified <paramref name="age"/>.
        /// </summary>
        /// <param name="age">The age after which searches are eligible for pruning, in minutes.</param>
        /// <returns>The number of pruned records.</returns>
        public async Task<int> PruneAsync(int age)
        {
            try
            {
                using var context = ContextFactory.CreateDbContext();

                var cutoffDateTime = DateTime.UtcNow.AddMinutes(-age);

                // unlike other pruning operations, we don't care about state, since there's a 60 minute minimum
                // and searches are guaranteed to be at least 60 minutes old by the time they can be pruned, they will
                // be completed unless someone applied some rather dumb settings
                var expired = context.Searches
                    .Where(s => s.EndedAt.HasValue && s.EndedAt.Value < cutoffDateTime)
                    .WithoutResponses()
                    .ToList();

                // defer the deletion to DeleteAsync() so that SignalR broadcasting works properly and the UI
                // is updated in real time
                foreach (var search in expired)
                {
                    await DeleteAsync(search);
                }

                return expired.Count;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to prune searches: {Message}", ex.Message);
                throw;
            }
        }
    }
}