"""Read-only comparison of supplied chart history and research warmup."""
import argparse
import json
from pathlib import Path
import pandas as pd
from gc_research.mgc_live_signals import normalized_minutes
from gc_research.mgc_sim_execution_v3 import ExecutionConfig
from tools.run_mgc_research_sim_v4 import bridge_directory,load_native,read_complete_csv


def compare(warm,chart,symbol):
    chart=normalized_minutes(chart)
    times=warm.index.intersection(chart.index)
    times=times[(warm.loc[times,'instrument'].astype(str).to_numpy()==symbol)&(chart.loc[times,'instrument'].astype(str).to_numpy()==symbol)]
    fields={};samples=[]
    for name in ['open','high','low','close','volume']:
        difference=chart.loc[times,name].astype(float)-warm.loc[times,name].astype(float)
        bad=difference.abs()>=1e-7
        fields[name]=dict(mismatching_minutes=int(bad.sum()),maximum_absolute_difference=float(difference.abs().max()) if len(times) else None,
            constant_difference=round(float(difference.iloc[0]),8) if len(times) and difference.round(8).nunique()==1 else None)
        if bad.any():
            for t in difference.index[bad][:2]:
                samples.append(dict(time_utc=t.isoformat(),field=name,saved=float(warm.loc[t,name]),chart=float(chart.loc[t,name])))
    return dict(mode='READ_ONLY_HISTORY_DIAGNOSTIC',instrument=symbol,overlapping_same_contract_minutes=len(times),
        chart_rows=len(chart),chart_sessions=int(chart.session_date.nunique()),
        fields=fields,samples=samples,merge_policy_not_verified_by_this_check=True,
        originals_modified=False,orders_sent=False,
        limits='A mismatch identifies differing inputs. It does not certify which provider history is correct, authorize adjusting prices, or validate profitability on the chart feed.')


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--root',type=Path,default=Path(__file__).resolve().parent);args=parser.parse_args()
    folder,state=load_native(bridge_directory(),ExecutionConfig())
    warm=normalized_minutes(pd.read_csv(args.root/'data/native_minutes.csv'))
    chart=read_complete_csv(folder/'minutes.csv')
    chart=chart.loc[pd.to_datetime(chart.time_utc,utc=True)<=state['last_bar_utc']]
    result=compare(warm,chart,state['instrument'])
    result.update(native_run_id=state['run_id'],native_last_closed_utc=state['last_bar_utc'].isoformat())
    output=args.root/'chart_history_diagnostic.json';output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2));print('Saved '+str(output))


if __name__=='__main__':main()
