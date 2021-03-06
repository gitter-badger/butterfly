﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Butterfly.DataContract.Tracing;
using Butterfly.Storage.Query;
using Nest;
using ISpanQuery = Butterfly.Storage.ISpanQuery;

namespace Butterfly.Elasticsearch
{
    public class ElasticsearchSpanQuery : ISpanQuery
    {
        private readonly ElasticClient _elasticClient;
        private readonly IIndexManager _indexManager;

        public ElasticsearchSpanQuery(IElasticClientFactory elasticClientFactory, IIndexManager indexManager)
        {
            _indexManager = indexManager;
            _elasticClient = elasticClientFactory.Create();
        }

        public async Task<Span> GetSpan(string spanId)
        {
            var index = Indices.Index(_indexManager.CreateTracingIndex());
            var spanResult = await _elasticClient.SearchAsync<Span>(s => s.Index(index).Query(q => q.Term(t => t.Field(f => f.SpanId).Value(spanId))));
            return spanResult.Documents.FirstOrDefault();
        }

        public Task<Trace> GetTrace(string traceId)
        {
            if (string.IsNullOrEmpty(traceId))
            {
                return Task.FromResult(new Trace { TraceId = traceId, Spans = new List<Span>() });
            }
            var index = Indices.Index(_indexManager.CreateTracingIndex());
            var trace = GetTrace(traceId, index);
            return Task.FromResult(trace);
        }

        public async Task<IEnumerable<Trace>> GetTraces(TraceQuery traceQuery)
        {
            var index = Indices.Index(_indexManager.CreateTracingIndex());

            var query = BuildTracesQuery(traceQuery);

            var traceIdsAggregationsResult = await _elasticClient.SearchAsync<Span>(s => s.Index(index).Size(0).Query(query).
                 Aggregations(a => a.Terms("group_by_traceId",
                 t => t.Aggregations(sub => sub.Min("min_startTimestapm", m => m.Field(f => f.StartTimestamp))).Field(f => f.TraceId).Order(o => o.Descending("min_startTimestapm")).Size(traceQuery.Limit))));

            var traceIdsAggregations = traceIdsAggregationsResult.Aggregations.FirstOrDefault().Value as BucketAggregate;

            if (traceIdsAggregations == null)
            {
                return new Trace[0];
            }

            var KeyedBuckets = traceIdsAggregations.Items.OfType<KeyedBucket<object>>();

            var traceIds = traceIdsAggregations.Items.OfType<KeyedBucket<object>>().Where(x => x.Key != null).Select(x => x.Key.ToString()).ToList();

            var limit = KeyedBuckets.Sum(x => x.DocCount).Value;

            if (traceIds.Count == 0)
            {
                return new Trace[0];
            }

            var spanResult = await _elasticClient.SearchAsync<Span>(s => s.Index(index).Size((int)limit).Query(q => q.ConstantScore(c => c.Filter(filter => filter.Terms(t => t.Field(f => f.TraceId).Terms(traceIds))))));

            return spanResult.Documents.GroupBy(x => x.TraceId).Select(x => new Trace { TraceId = x.Key, Spans = x.ToList() }).OrderByDescending(x => x.Spans.Min(s => s.StartTimestamp)).ToList();

            //var traces = traceIdsAggregations.Items.OfType<KeyedBucket<object>>().AsParallel().Select(x => GetTrace(x.Key?.ToString(), index)).OrderByDescending(x => x.Spans.Min(s => s.StartTimestamp)).ToList();

            //return traces;
        }

        public async Task<IEnumerable<Span>> GetSpanDependencies(DependencyQuery dependencyQuery)
        {
            var index = Indices.Index(_indexManager.CreateTracingIndex());

            var spanResult = await _elasticClient.SearchAsync<Span>(s => s.Index(index).Size(2048).Query(query => query.Bool(b => b.Must(BuildMustQuery(dependencyQuery)))));

            return spanResult.Documents;
        }

        private Func<QueryContainerDescriptor<Span>, QueryContainer> BuildTracesQuery(TraceQuery traceQuery)
        {
            return query => query.Bool(b => b.Must(BuildMustQuery(traceQuery)));
        }

        private IEnumerable<Func<QueryContainerDescriptor<Span>, QueryContainer>> BuildMustQuery(TraceQuery traceQuery)
        {
            if (traceQuery.StartTimestamp != null)
            {
                yield return q => q.DateRange(d => d.Field(x => x.StartTimestamp).GreaterThanOrEquals(traceQuery.StartTimestamp.Value.DateTime));
            }

            if (traceQuery.FinishTimestamp != null)
            {
                yield return q => q.DateRange(d => d.Field(x => x.FinishTimestamp).LessThanOrEquals(traceQuery.FinishTimestamp.Value.DateTime));
            }

            foreach (var queryTag in BuildQueryTags(traceQuery))
            {
                yield return q => q.Nested(n => n.Path(x => x.Tags).Query(q1 => q1.Bool(b => b.Must(f => f.Term(new Field("tags.key"), queryTag.Key?.ToLower()), f => f.Term(new Field("tags.value"), queryTag.Value?.ToLower())))));
            }
        }

        private IEnumerable<Tag> BuildQueryTags(TraceQuery traceQuery)
        {
            if (!string.IsNullOrEmpty(traceQuery.ServiceName))
            {
                yield return new Tag { Key = QueryConstants.Service, Value = traceQuery.ServiceName };
            }

            if (!string.IsNullOrEmpty(traceQuery.Tags))
            {
                var tags = traceQuery.Tags.Split('|');
                foreach (var tag in tags)
                {
                    var pair = tag.Split('=');
                    if (pair.Length == 2)
                    {
                        yield return new Tag { Key = pair[0], Value = pair[1] };
                    }
                }
            }
        }

        private IEnumerable<Func<QueryContainerDescriptor<Span>, QueryContainer>> BuildMustQuery(DependencyQuery dependencyQuery)
        {
            if (dependencyQuery.StartTimestamp != null)
            {
                yield return q => q.DateRange(d => d.Field(x => x.StartTimestamp).GreaterThanOrEquals(dependencyQuery.StartTimestamp.Value.DateTime));
            }

            if (dependencyQuery.FinishTimestamp != null)
            {
                yield return q => q.DateRange(d => d.Field(x => x.FinishTimestamp).LessThanOrEquals(dependencyQuery.FinishTimestamp.Value.DateTime));
            }
        }

        private Trace GetTrace(string traceId, Indices index)
        {
            var count = _elasticClient.Count<Span>(s => s.Index(index).Query(q => q.Term(t => t.Field(f => f.TraceId).Value(traceId)))).Count;
            var spans = GetSpans(traceId, (int)count, index);
            return new Trace
            {
                TraceId = traceId,
                Spans = spans.ToList()
            };
        }

        private IEnumerable<Span> GetSpans(string traceId, int size, Indices index)
        {
            return _elasticClient.Search<Span>(s => s.Index(index).Size(size).Query(q => q.Term(t => t.Field(f => f.TraceId).Value(traceId)))).Documents;
        }
    }
}