﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Validation;
using System.Threading.Tasks;
using Conditions;

namespace DDD.Core.Infrastructure.Data
{
    using Mapping;
    using Infrastructure;
    using Domain;

    public abstract class EFRepository<TDomainEntity, TStateEntity, TContext>
        : IAsyncRepository<TDomainEntity>
        where TDomainEntity : DomainEntity, IStateObjectConvertible<TStateEntity>
        where TStateEntity : class, IStateEntity, new()
        where TContext : StateEntitiesContext
    {

        #region Constructors

        protected EFRepository(IObjectTranslator<TStateEntity, TDomainEntity> entityTranslator,
                               IDbContextFactory<TContext> contextFactory)
        {
            Condition.Requires(entityTranslator, nameof(entityTranslator)).IsNotNull();
            Condition.Requires(contextFactory, nameof(contextFactory)).IsNotNull();
            this.EntityTranslator = entityTranslator;
            this.ContextFactory = contextFactory;
        }

        #endregion Constructors

        #region Properties

        protected IDbContextFactory<TContext> ContextFactory { get; }

        protected IObjectTranslator<TStateEntity, TDomainEntity> EntityTranslator { get; }

        #endregion Properties

        #region Methods

        public async virtual Task SaveAsync(TDomainEntity aggregate)
        {
            Condition.Requires(aggregate, nameof(aggregate)).IsNotNull();
            using (var context = this.CreateContext())
            {
                context.Set<TStateEntity>().Add(aggregate.ToState());
                await SaveChangesAsync(context);
            }
        }

        public async virtual Task SaveAllAsync(IEnumerable<TDomainEntity> aggregates)
        {
            Condition.Requires(aggregates, nameof(aggregates))
                     .IsNotNull()
                     .IsNotEmpty()
                     .DoesNotContain(null);
            using (var context = this.CreateContext())
            {
                context.Set<TStateEntity>().AddRange(aggregates.Select(a => a.ToState()));
                await SaveChangesAsync(context);
            }
        }

        public async Task<TDomainEntity> FindAsync(params ComparableValueObject[] identityComponents)
        {
            Condition.Requires(identityComponents, nameof(identityComponents))
                     .IsNotNull()
                     .IsNotEmpty()
                     .DoesNotContain(null);
            using (var context = this.CreateContext(onSave: false))
            {
                var keyNames = context.GetKeyNames<TStateEntity>();
                var keyValues = identityComponents.Select(c => c.EqualityComponents().First());
                var query = context.Set<TStateEntity>().AsQueryable();
                foreach (var path in this.RelatedEntitiesPaths()) query = query.Include(path);
                var findExpression = BuildFindExpression(keyNames, keyValues);
                var stateEntity = await query.FirstOrDefaultAsync(findExpression);
                if (stateEntity == null) return null;
                return this.EntityTranslator.Translate(stateEntity);
            }
        }

        protected TContext CreateContext(bool onSave = true)
        {
            try
            {
                return this.ContextFactory.CreateContext();
            }
            catch (DbException ex) when (onSave == true)
            {
                throw new RepositoryException($"A problem occurred while saving a domain entity of type '{typeof(TDomainEntity)}'.", ex);
            }
            catch (DbException ex) when (onSave == false)
            {
                throw new RepositoryException($"A problem occurred while finding a domain entity of type '{typeof(TDomainEntity)}'.", ex);
            }
        }

        protected static async Task SaveChangesAsync(TContext context)
        {
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new RepositoryConcurrencyException($"A concurrency conflict occurred while saving a domain entity of type '{typeof(TDomainEntity)}'.", ex);
            }
            catch (Exception ex) when (ex is DbUpdateException || ex is DbEntityValidationException)
            {
                throw new RepositoryException($"A problem occurred while saving a domain entity of type '{typeof(TDomainEntity)}'.", ex);
            }
        }

        protected abstract IEnumerable<Expression<Func<TStateEntity, object>>> RelatedEntitiesPaths();

        private static Expression<Func<TStateEntity, bool>> BuildFindExpression(IEnumerable<string> keyNames, IEnumerable<object> keyValues)
        {
            Condition.Requires(keyNames, nameof(keyNames))
                     .IsNotNull()
                     .IsNotEmpty()
                     .HasLength(keyValues.Count());
            var entity = Expression.Parameter(typeof(TStateEntity), "entity");
            Expression find = null;
            for (int i = 0; i < keyNames.Count(); i++)
            {
                var KeyName = Expression.Property(entity, keyNames.ElementAt(i));
                var keyValue = Expression.Constant(keyValues.ElementAt(i));
                var equals = KeyName.Type.GetMethod("Equals", new[] { KeyName.Type });
                var keyNameEqualsKeyValue = Expression.Call(KeyName, equals, keyValue);
                if (find == null)
                    find = keyNameEqualsKeyValue;
                else
                    find = Expression.AndAlso(find, keyNameEqualsKeyValue);
            }
            return Expression.Lambda<Func<TStateEntity, bool>>(find, entity);
        }

        #endregion Methods

    }
}